using System;
using Dalamud.Plugin;
using Glamourer.Api.Enums;
using Glamourer.Api.Helpers;
using Glamourer.Api.IpcSubscribers;
using Newtonsoft.Json.Linq;

namespace HMSync.Services;

/// <summary>
/// S248: wrapper over the Glamourer IPC for the cosmetic VISIBILITY toggles
/// (weapon / hat / visor). Glamourer is AUTHORITATIVE - HMS reads state from it, writes to it,
/// and refreshes on its StateChanged event. HMS keeps NO tracked toggle state of its own, so the
/// HMS badges always reflect real Glamourer state and can never go stale (the S246/S247 stale-bit
/// bug is structurally impossible here). When Glamourer is absent, the caller falls back to HMS's
/// native DrawData path for writes and hides the badges.
///
/// All signatures decompiled from the shipped Glamourer.Api.dll (2.x):
///   GlamourerApiEc SetMetaState.Invoke(int objectIndex, MetaFlag types, bool newValue,
///                                      uint key, ApplyFlag flags)
///   (GlamourerApiEc, JObject) GetState.Invoke(int objectIndex, uint key)
///   EventSubscriber&lt;nint&gt; StateChanged.Subscriber(pi, params Action&lt;nint&gt;[])  // nint = actor addr
///   (int Major, int Minor) ApiVersion.Invoke()
/// MetaFlag: None, Wetness, HatState, VisorState, WeaponState, EarState.
///
/// DEPLOYMENT: Glamourer.Api.dll MUST ship next to HMSync.dll (PackageReference copies it). Do NOT
/// use AssemblyLoadContext - IPC crosses the ALC boundary as primitives, so HMS's copy never
/// conflicts with Glamourer's loaded copy.
/// </summary>
public sealed class GlamourerIpc : IDisposable
{
    private readonly ApiVersion _apiVersion;
    private readonly SetMetaState _setMetaState;
    private readonly GetState _getState;
    private readonly EventSubscriber<nint> _stateChanged;

    /// <summary>
    /// Raised when Glamourer reports a state change for the actor at this address (nint = actor
    /// pointer). The caller filters for the local player and re-reads via TryGetMeta.
    /// </summary>
    public event Action<nint>? StateChanged;

    public GlamourerIpc(IDalamudPluginInterface pi)
    {
        _apiVersion = new ApiVersion(pi);
        _setMetaState = new SetMetaState(pi);
        _getState = new GetState(pi);
        // Subscribe to Glamourer's StateChanged; forward the actor address to our event.
        _stateChanged = global::Glamourer.Api.IpcSubscribers.StateChanged.Subscriber(pi, OnGlamourerStateChanged);
    }

    private void OnGlamourerStateChanged(nint actorAddress)
        => StateChanged?.Invoke(actorAddress);

    /// <summary>
    /// True if Glamourer is loaded and answering. If the version call throws, the subscriber
    /// isn't registered → Glamourer absent → caller uses the HMS fallback / hides badges.
    /// </summary>
    public bool Available
    {
        get
        {
            try { return _apiVersion.Invoke().Item1 > 0; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Set one meta-visibility flag on an actor (objectIndex 0 = local player). Sticky, unlocked.
    /// Returns true on a Success result. NOTE: Glamourer's flags are VISIBILITY (true = shown);
    /// HMS's internal bools are HIDDEN (true = hidden). Caller inverts where needed.
    /// </summary>
    public bool SetMeta(int objectIndex, MetaFlag flag, bool visible)
    {
        try
        {
            var ec = _setMetaState.Invoke(objectIndex, flag, visible, key: 0, flags: ApplyFlag.Equipment);
            return ec == GlamourerApiEc.Success;
        }
        catch { return false; }
    }

    /// <summary>
    /// Read the three meta-visibility flags from Glamourer for the given actor (0 = local player).
    /// Returns false if Glamourer is absent, the call fails, or the state can't be parsed - in
    /// which case the caller shows an unknown/neutral badge rather than wrong data.
    /// Each out-param is VISIBILITY (true = shown), matching Glamourer's own checkbox sense.
    /// </summary>
    public bool TryGetMeta(int objectIndex, out bool weaponVisible, out bool hatVisible, out bool visorToggled)
    {
        weaponVisible = hatVisible = visorToggled = false;
        try
        {
            var (ec, jObj) = _getState.Invoke(objectIndex, key: 0);
            if (ec != GlamourerApiEc.Success || jObj == null)
                return false;
            return TryParseMeta(jObj, out weaponVisible, out hatVisible, out visorToggled);
        }
        catch { return false; }
    }

    /// <summary>
    /// Parse the meta-toggle booleans out of Glamourer's state JObject.
    ///
    /// JSON KEY PATHS - read directly from Glamourer's source (DesignBase.SerializeEquipment):
    ///   root["Equipment"]["Hat"]["Show"]        -> bool   (hat visible;    from IsHatVisible())
    ///   root["Equipment"]["Weapon"]["Show"]     -> bool   (weapon visible; from IsWeaponVisible())
    ///   root["Equipment"]["Visor"]["IsToggled"] -> bool   (visor toggled;  from IsVisorToggled())
    /// All three out-params are VISIBILITY/toggle sense (true = shown / visor-up), matching
    /// Glamourer's own checkboxes.
    ///
    /// NON-HUMAN GUARD: when the actor isn't a human model, Glamourer serializes Equipment as a
    /// flat base64 "Array" blob with NO Hat/Visor/Weapon objects. In that case the toggles aren't
    /// readable → return false so the caller shows an unknown badge rather than fabricating "off".
    /// </summary>
    private static bool TryParseMeta(JObject root, out bool weaponVisible, out bool hatVisible, out bool visorToggled)
    {
        weaponVisible = hatVisible = visorToggled = false;

        if (root["Equipment"] is not JObject equip)
            return false;

        // Non-human model path: Equipment is a base64 "Array" with no toggle objects → unreadable.
        if (equip["Array"] != null && equip["Hat"] == null)
            return false;

        bool any = false;
        if (equip["Hat"] is JObject hat && hat["Show"] is { Type: JTokenType.Boolean } hatShow)
        { hatVisible = hatShow.Value<bool>(); any = true; }
        if (equip["Weapon"] is JObject wpn && wpn["Show"] is { Type: JTokenType.Boolean } wpnShow)
        { weaponVisible = wpnShow.Value<bool>(); any = true; }
        if (equip["Visor"] is JObject vis && vis["IsToggled"] is { Type: JTokenType.Boolean } visTok)
        { visorToggled = visTok.Value<bool>(); any = true; }

        return any;
    }

    /// <summary>
    /// S248 PROBE: return the raw Glamourer state JObject as an indented string, for confirming the
    /// JSON key paths against the live Glamourer build (see TryParseMeta). Null if unavailable.
    /// Dev-only; remove once keys are locked.
    /// </summary>
    public string? DumpRawState(int objectIndex)
    {
        try
        {
            var (ec, jObj) = _getState.Invoke(objectIndex, key: 0);
            if (ec != GlamourerApiEc.Success || jObj == null)
                return "[GetState ec=" + ec + ", jObj null=" + (jObj == null) + "]";
            return jObj.ToString(Newtonsoft.Json.Formatting.Indented);
        }
        catch (Exception ex) { return "[DumpRawState threw: " + ex.Message + "]"; }
    }

    public void Dispose()
    {
        try { _stateChanged.Dispose(); } catch { /* ignore */ }
    }
}
