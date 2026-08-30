namespace HMSync.Services;

using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
// HMS ↔ HDM (mob-disguise) integration — the CONSUMER half of HDM's IPC provider (HGuise/HdmIpc.cs, v1.0).
//
// HDM is a self-apply-only client-side disguise plugin. Its provider lets a courier (HMS) (a) OBSERVE this DM's
// disguise + puppet state so it can sync it to a room, and (b) DRIVE this client's actors to MIRROR what a remote
// DM is showing. This is the exact GlamourerIpc/MonikerService pattern: bind labels by string, gate on
// HDM.ApiVersion (major), degrade soft when absent (every call guarded → no HDM = inert feature).
//
// ⚠ THE ALC BOUNDARY. Structured payloads cross as JSON STRINGS (Newtonsoft, PascalCase) — a struct defined in
// HDM is NOT type-identical to HMS's mirror across the AssemblyLoadContext boundary, so both sides (de)serialize.
// The property NAMES on the mirror types below ARE the contract (HGuise/docs/HDM-sync-IPC-decisions.md §E5).
//
// This class is ONLY the IPC boundary: JSON⇄POCO + label binding. It does NOT touch the relay wire, identity
// resolution, or lifecycle — that's DisguiseSyncService's job. Outbound HDM signals surface as C# events (the
// SENDER feeds them onto the wire); receiver methods drive a peer/puppet (the RECEIVER calls them).
// See docs/HDM-sync-HMS-side-brief.md §3.0/§3.1/§3.3.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>HMS-side mirror of HDM's DisguiseAtom (HGuise/HdmIpc.cs). Envelope-LESS — the source identity
/// (SenderContentId), SubjectId and Seq are HMS wire concerns stamped by DisguiseSyncService, not carried across
/// the IPC. Property names must match HDM's exactly (Newtonsoft binds by name). Additive fields default-decode.</summary>
public sealed class HdmDisguiseAtom
{
    public uint Epoch { get; set; }         // per-source monotonic (HDM authors); receiver applies iff ≥ last for the subject
    public byte Kind { get; set; }          // 1 Human / 2 Demihuman / 3 Monster; 0 = REVERT (disguise off)
    public uint BaseId { get; set; }        // BNpcBase (<1e6) or ENpcBase (>=1e6); receiver resolves equip/customize locally
    public int ModelCharaId { get; set; }   // render key (authoritative for Monster; carried for Demi/Human)
    public float Scale { get; set; }        // resolved absolute multiplier
    public float VOffset { get; set; }      // vertical draw offset, world units (F2: apply-time only, no per-frame emit)
    public ushort LoopId { get; set; }      // held animation timeline (Timeline.BaseOverride); 0 = none
}

/// <summary>HMS mirror of HDM's DisguiseChanged payload — a subject discriminator + the atom. Slot null = the DM's
/// own body; Slot N = the DM's puppet ordinal N (a never-reset per-puppet serial HMS namespaces under ContentId).</summary>
public sealed class HdmDisguiseChange
{
    public int? Slot { get; set; }
    public HdmDisguiseAtom Atom { get; set; } = new();
}

/// <summary>HMS mirror of HDM's PuppetInfo — one spawned puppet as the source sees it. ObjectIndex is the SOURCE's
/// local index (meaningless on this client); HMS routes by Slot (namespaced under the owner ContentId). Flat Px/Py/Pz.</summary>
public sealed class HdmPuppetInfo
{
    public int Slot { get; set; }
    public int ObjectIndex { get; set; }
    public HdmDisguiseAtom Atom { get; set; } = new();
    public float Px { get; set; }
    public float Py { get; set; }
    public float Pz { get; set; }
    public float Rot { get; set; }
    public bool Frozen { get; set; }        // MinorVersion 4: this puppet's freeze-animation pin (late-join snapshot)
}

public sealed class HdmIpc : IDisposable
{
    private const uint RequiredMajor = 1;   // HDM HdmIpc.MajorVersion
    private const uint SanitizeSelfMinor = 3;  // HDM.SanitizeSelf own-body sanitiser landed at MinorVersion 3 (HDMT b8)
    private const uint FreezeMinor = 4;        // HDM.FreezeChanged/GetFrozenOwnBody/SetFrozen landed at MinorVersion 4 (HDMT b9)
    private uint minor;                     // HDM HdmIpc.MinorVersion captured at connect; gates additive (minor-bumped) calls

    private readonly IPluginLog log;
    private readonly IDalamudPluginInterface pi;

    // ── Version / lifecycle ──
    private ICallGateSubscriber<(uint, uint)>? apiVersion;
    private ICallGateSubscriber<object?>? onReady;       // HDM.Ready
    private ICallGateSubscriber<object?>? onDisposing;   // HDM.Disposing

    // ── Outbound events (HDM → HMS; we .Subscribe) ──
    private ICallGateSubscriber<string, object?>? evDisguiseChanged;                       // JSON {Slot:int?, Atom}
    private ICallGateSubscriber<int, uint, object?>? evActionFired;                        // (slot: -1 = own body, playId)
    private ICallGateSubscriber<string, object?>? evPuppetSpawned;                         // JSON PuppetInfo
    private ICallGateSubscriber<int, object?>? evPuppetReady;                              // (objectIndex)
    private ICallGateSubscriber<int, object?>? evPuppetDespawned;                          // (slot)
    private ICallGateSubscriber<int, float, float, float, float, object?>? evPuppetMoved;  // (slot,x,y,z,rot)
    private ICallGateSubscriber<bool, object?>? evOwnBodyHidden;                           // Bug B (v1.1): DM own-body hide edge
    private ICallGateSubscriber<int, bool, object?>? evFreezeChanged;                      // MinorVersion 4: (slot: -1 = own body, frozen)

    // ── Snapshot getters (HMS pull) ──
    private ICallGateSubscriber<string>? getDisguise;   // JSON atom, or "" if none
    private ICallGateSubscriber<string>? getPuppets;    // JSON PuppetInfo[], or "[]"
    private ICallGateSubscriber<bool>? getOwnBodyHidden; // Bug B (v1.1): current DM own-body hide state (late-join snapshot)
    private ICallGateSubscriber<bool>? getFrozenOwnBody; // MinorVersion 4: current DM own-body freeze state (late-join snapshot)

    // ── Receiver methods (HMS → HDM) ──
    private ICallGateSubscriber<int, string, object?>? applyDisguise;                       // (objectIndex, atomJson)
    private ICallGateSubscriber<int, object?>? revertDisguise;                              // (objectIndex)
    private ICallGateSubscriber<bool, object?>? sanitizeSelf;                               // (restoreVisual) own-body sanitiser, minor>=3
    private ICallGateSubscriber<int, bool, object?>? setFrozen;                             // (objectIndex, frozen) freeze-anim pin, minor>=4
    private ICallGateSubscriber<int, uint, object?>? playAction;                            // (objectIndex, playId)
    private ICallGateSubscriber<string, int>? spawnPuppet;                                  // (atomJson) -> objectIndex, -1 fail
    private ICallGateSubscriber<int, float, float, float, float, object?>? movePuppet;      // (idx,x,y,z,rot)
    private ICallGateSubscriber<int, object?>? despawnPuppet;                               // (objectIndex)

    public bool Available { get; private set; }

    // ── Outbound signals, surfaced as C# events. The SENDER (DisguiseSyncService) stamps envelope + puts on the wire.
    public event Action<int?, HdmDisguiseAtom>? OnDisguiseChanged;   // (slot: null = own body, atom)
    public event Action<int, uint>? OnActionFired;                  // (slot: -1 = own body, playId)
    public event Action<HdmPuppetInfo>? OnPuppetSpawned;
    public event Action<int>? OnPuppetReady;                        // (objectIndex — SOURCE-local)
    public event Action<int>? OnPuppetDespawned;                    // (slot)
    public event Action<int, float, float, float, float>? OnPuppetMoved;   // (slot,x,y,z,rot)
    // Bug B (v1.1): the DM's own body just became hidden (true) / visible (false). Fires only on a CHANGE (HDM dedupes);
    // the SENDER puts the bit on the wire so peers suppress the DM's own-body mirror while it drives a possessed puppet.
    public event Action<bool>? OnOwnBodyHidden;
    // MinorVersion 4: a subject's freeze-animation pin just toggled. slot -1 = the DM's own body, N = puppet ordinal.
    // The SENDER puts the per-subject bit on the wire so peers freeze/release the matching mirror actor.
    public event Action<int, bool>? OnFreezeChanged;   // (slot: -1 = own body, frozen)
    // Fired when HDM (re)announces itself, so the sync layer can re-pull snapshots / re-broadcast own state.
    public event Action? OnHdmReady;

    public HdmIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.pi = pi;
        this.log = log;

        // Bind + subscribe the OUTBOUND event gates unconditionally (harmless if HDM is absent — SendMessage just
        // never fires). These stay live across HDM reloads. Wrapped so a bad bind can't abort ctor.
        try
        {
            evDisguiseChanged = pi.GetIpcSubscriber<string, object?>("HDM.DisguiseChanged");
            evDisguiseChanged.Subscribe(OnDisguiseChangedRaw);
            evActionFired = pi.GetIpcSubscriber<int, uint, object?>("HDM.ActionFired");
            evActionFired.Subscribe(OnActionFiredRaw);
            evPuppetSpawned = pi.GetIpcSubscriber<string, object?>("HDM.PuppetSpawned");
            evPuppetSpawned.Subscribe(OnPuppetSpawnedRaw);
            evPuppetReady = pi.GetIpcSubscriber<int, object?>("HDM.PuppetReady");
            evPuppetReady.Subscribe(OnPuppetReadyRaw);
            evPuppetDespawned = pi.GetIpcSubscriber<int, object?>("HDM.PuppetDespawned");
            evPuppetDespawned.Subscribe(OnPuppetDespawnedRaw);
            evPuppetMoved = pi.GetIpcSubscriber<int, float, float, float, float, object?>("HDM.PuppetMoved");
            evPuppetMoved.Subscribe(OnPuppetMovedRaw);
            // Bug B (v1.1): additive - safe to bind unconditionally against a pre-v1.1 HDM (it simply never fires it).
            evOwnBodyHidden = pi.GetIpcSubscriber<bool, object?>("HDM.OwnBodyHidden");
            evOwnBodyHidden.Subscribe(OnOwnBodyHiddenRaw);
            // MinorVersion 4: additive - safe to bind unconditionally against a pre-b9 HDM (it simply never fires it).
            evFreezeChanged = pi.GetIpcSubscriber<int, bool, object?>("HDM.FreezeChanged");
            evFreezeChanged.Subscribe(OnFreezeChangedRaw);

            onReady = pi.GetIpcSubscriber<object?>("HDM.Ready");
            onReady.Subscribe(OnHdmReadyRaw);
            onDisposing = pi.GetIpcSubscriber<object?>("HDM.Disposing");
            onDisposing.Subscribe(OnHdmDisposingRaw);
        }
        catch (Exception ex) { log.Debug("[HMSync] HDM IPC event subscribe failed: " + ex.Message); }

        TryConnect();
    }

    // ── Lifecycle re-probe (either load order) ──
    private void OnHdmReadyRaw() { TryConnect(); if (Available) { log.Information("[HMSync] HDM detected - disguise sync active."); OnHdmReady?.Invoke(); } }
    private void OnHdmDisposingRaw() { Available = false; log.Information("[HMSync] HDM went away - disguise sync inert."); }

    private void TryConnect()
    {
        try
        {
            apiVersion = pi.GetIpcSubscriber<(uint, uint)>("HDM.ApiVersion");
            var (major, minorV) = apiVersion.InvokeFunc();
            if (major != RequiredMajor)
            {
                Available = false;
                log.Information($"[HMSync] HDM present but API v{major} != required v{RequiredMajor} - disguise sync off.");
                return;
            }
            minor = minorV;   // gate additive calls (SanitizeSelf) on this rather than probe-by-throw
            getDisguise = pi.GetIpcSubscriber<string>("HDM.GetDisguise");
            getPuppets = pi.GetIpcSubscriber<string>("HDM.GetPuppets");
            getOwnBodyHidden = pi.GetIpcSubscriber<bool>("HDM.GetOwnBodyHidden");   // Bug B (v1.1)
            getFrozenOwnBody = pi.GetIpcSubscriber<bool>("HDM.GetFrozenOwnBody");   // minor>=4
            applyDisguise = pi.GetIpcSubscriber<int, string, object?>("HDM.ApplyDisguise");
            revertDisguise = pi.GetIpcSubscriber<int, object?>("HDM.RevertDisguise");
            sanitizeSelf = pi.GetIpcSubscriber<bool, object?>("HDM.SanitizeSelf");   // minor>=3; guarded at call site
            setFrozen = pi.GetIpcSubscriber<int, bool, object?>("HDM.SetFrozen");    // minor>=4; guarded at call site
            playAction = pi.GetIpcSubscriber<int, uint, object?>("HDM.PlayAction");
            spawnPuppet = pi.GetIpcSubscriber<string, int>("HDM.SpawnPuppet");
            movePuppet = pi.GetIpcSubscriber<int, float, float, float, float, object?>("HDM.MovePuppet");
            despawnPuppet = pi.GetIpcSubscriber<int, object?>("HDM.DespawnPuppet");
            Available = true;
        }
        catch
        {
            // Not installed / not ready yet → inert. Not an error (optional integration).
            Available = false;
        }
    }

    // ── Outbound raw handlers: deserialize the IPC JSON/scalars into HMS POCOs, raise the typed event. ──
    private void OnDisguiseChangedRaw(string json)
    {
        try
        {
            var d = JsonConvert.DeserializeObject<HdmDisguiseChange>(json);
            if (d != null) OnDisguiseChanged?.Invoke(d.Slot, d.Atom ?? new HdmDisguiseAtom());
        }
        catch (Exception ex) { log.Debug("[HMSync] HDM DisguiseChanged decode failed: " + ex.Message); }
    }

    private void OnActionFiredRaw(int slot, uint playId)
    {
        try { OnActionFired?.Invoke(slot, playId); }
        catch (Exception ex) { log.Debug("[HMSync] HDM ActionFired handler failed: " + ex.Message); }
    }

    private void OnPuppetSpawnedRaw(string json)
    {
        try
        {
            var p = JsonConvert.DeserializeObject<HdmPuppetInfo>(json);
            if (p != null) OnPuppetSpawned?.Invoke(p);
        }
        catch (Exception ex) { log.Debug("[HMSync] HDM PuppetSpawned decode failed: " + ex.Message); }
    }

    private void OnPuppetReadyRaw(int objectIndex) { try { OnPuppetReady?.Invoke(objectIndex); } catch (Exception ex) { log.Debug("[HMSync] HDM PuppetReady handler failed: " + ex.Message); } }
    private void OnPuppetDespawnedRaw(int slot) { try { OnPuppetDespawned?.Invoke(slot); } catch (Exception ex) { log.Debug("[HMSync] HDM PuppetDespawned handler failed: " + ex.Message); } }
    private void OnPuppetMovedRaw(int slot, float x, float y, float z, float rot) { try { OnPuppetMoved?.Invoke(slot, x, y, z, rot); } catch (Exception ex) { log.Debug("[HMSync] HDM PuppetMoved handler failed: " + ex.Message); } }
    private void OnOwnBodyHiddenRaw(bool hidden) { try { OnOwnBodyHidden?.Invoke(hidden); } catch (Exception ex) { log.Debug("[HMSync] HDM OwnBodyHidden handler failed: " + ex.Message); } }
    private void OnFreezeChangedRaw(int slot, bool frozen) { try { OnFreezeChanged?.Invoke(slot, frozen); } catch (Exception ex) { log.Debug("[HMSync] HDM FreezeChanged handler failed: " + ex.Message); } }

    // ── Snapshot getters (late-join / first-sight; safety net for a mid-session HMS reload) ──
    /// <summary>The source's own-body disguise, or null if none (HDM returns "" for none).</summary>
    public HdmDisguiseAtom? GetDisguise()
    {
        if (!Available || getDisguise == null) return null;
        try
        {
            var json = getDisguise.InvokeFunc();
            if (string.IsNullOrEmpty(json)) return null;
            return JsonConvert.DeserializeObject<HdmDisguiseAtom>(json);
        }
        catch (Exception ex) { log.Debug("[HMSync] HDM GetDisguise failed: " + ex.Message); return null; }
    }

    /// <summary>Bug B (v1.1): the source DM's CURRENT own-body hide state. Call on first-sight of this source (late-join)
    /// so a peer that starts mirroring mid-possession begins suppressed. A pre-v1.1 HDM has no getter → throws → false.</summary>
    public bool GetOwnBodyHidden()
    {
        if (!Available || getOwnBodyHidden == null) return false;
        try { return getOwnBodyHidden.InvokeFunc(); }
        catch (Exception ex) { log.Debug("[HMSync] HDM GetOwnBodyHidden failed: " + ex.Message); return false; }
    }

    /// <summary>MinorVersion 4: the source DM's CURRENT own-body freeze-animation state. Call on late-join so a peer that
    /// starts mirroring mid-session begins frozen. A pre-b9 HDM has no getter → throws → false (unfrozen).</summary>
    public bool GetFrozenOwnBody()
    {
        if (!Available || getFrozenOwnBody == null) return false;
        try { return getFrozenOwnBody.InvokeFunc(); }
        catch (Exception ex) { log.Debug("[HMSync] HDM GetFrozenOwnBody failed: " + ex.Message); return false; }
    }

    /// <summary>Every live puppet the source owns (HDM returns "[]" for none).</summary>
    public List<HdmPuppetInfo> GetPuppets()
    {
        if (!Available || getPuppets == null) return new List<HdmPuppetInfo>();
        try
        {
            var json = getPuppets.InvokeFunc();
            if (string.IsNullOrEmpty(json)) return new List<HdmPuppetInfo>();
            return JsonConvert.DeserializeObject<List<HdmPuppetInfo>>(json) ?? new List<HdmPuppetInfo>();
        }
        catch (Exception ex) { log.Debug("[HMSync] HDM GetPuppets failed: " + ex.Message); return new List<HdmPuppetInfo>(); }
    }

    // ── Receiver methods (drive a local peer/puppet mirroring a remote DM). All no-op-safe: HDM's side is
    // exception-guarded and stale objectIndexes resolve to a no-op there. We still guard our own serialize. ──
    /// <summary>Apply (Kind 1/2/3) or revert (Kind 0) a disguise onto a local actor. HDM diffs vs its per-index
    /// last-applied → a loop-only delta skips the model redraw. Epoch-gated on HDM's side.</summary>
    public void ApplyDisguise(int objectIndex, HdmDisguiseAtom atom)
    {
        if (!Available || applyDisguise == null) return;
        try { applyDisguise.InvokeAction(objectIndex, JsonConvert.SerializeObject(atom)); }
        catch (Exception ex) { log.Debug("[HMSync] HDM ApplyDisguise failed: " + ex.Message); }
    }

    public void RevertDisguise(int objectIndex)
    {
        if (!Available || revertDisguise == null) return;
        try { revertDisguise.InvokeAction(objectIndex); }
        catch (Exception ex) { log.Debug("[HMSync] HDM RevertDisguise failed: " + ex.Message); }
    }

    /// <summary>HDM's own-body sanitiser (MinorVersion &gt;= 3, HDMT b8). Reverts the persistent leak fields that
    /// survive a logout/login round-trip — GameObject.Scale, the vertical DrawOffset, and the own-body hide — plus,
    /// when <paramref name="restoreVisual"/> is true, the model + Glamourer look via a redraw. Idempotent and
    /// edge-safe (HDM guards a dead/absent own-body internally), so HMS fires restoreVisual:false on its reliable
    /// logout edge as a belt-and-suspenders trigger dual with HDM's own OnLogout — whichever runs while the body is
    /// still live wins. Gated on minor: a pre-b8 HDM has no such gate, so this is inert there (that HDM's OnLogout
    /// still covers the model path; only the field leak is what b8 closes).</summary>
    public bool SanitizeSelfSupported => Available && sanitizeSelf != null && minor >= SanitizeSelfMinor;

    public void SanitizeSelf(bool restoreVisual)
    {
        if (!SanitizeSelfSupported) return;
        try { sanitizeSelf!.InvokeAction(restoreVisual); }
        catch (Exception ex) { log.Debug("[HMSync] HDM SanitizeSelf failed: " + ex.Message); }
    }

    /// <summary>MinorVersion 4 (HDMT b9): pin (frozen=true) or release a LOCAL actor's animation, mirroring a remote DM's
    /// freeze toggle. HDM drives AnimationService.SetSpeed and re-asserts the pin every frame via its two speed hooks, so
    /// the hold sticks without HMS poking it per-frame - HMS just re-calls on rebind/first-sight. Idempotent; a stale
    /// objectIndex no-ops HDM-side. Gated on minor>=4 → inert against a pre-b9 HDM or none.</summary>
    public bool SetFrozenSupported => Available && setFrozen != null && minor >= FreezeMinor;

    public void SetFrozen(int objectIndex, bool frozen)
    {
        if (!SetFrozenSupported) return;
        try { setFrozen!.InvokeAction(objectIndex, frozen); }
        catch (Exception ex) { log.Debug("[HMSync] HDM SetFrozen failed: " + ex.Message); }
    }

    public void PlayAction(int objectIndex, uint playId)
    {
        if (!Available || playAction == null) return;
        try { playAction.InvokeAction(objectIndex, playId); }
        catch (Exception ex) { log.Debug("[HMSync] HDM PlayAction failed: " + ex.Message); }
    }

    /// <summary>Spawn a local puppet mirroring a remote DM's puppet, guised as the atom once drawn. Returns the
    /// LOCAL objectIndex SYNCHRONOUSLY (usable at once for MovePuppet routing), or -1 on failure / HDM absent.</summary>
    public int SpawnPuppet(HdmDisguiseAtom atom)
    {
        if (!Available || spawnPuppet == null) return -1;
        try { return spawnPuppet.InvokeFunc(JsonConvert.SerializeObject(atom)); }
        catch (Exception ex) { log.Debug("[HMSync] HDM SpawnPuppet failed: " + ex.Message); return -1; }
    }

    public void MovePuppet(int objectIndex, float x, float y, float z, float rot)
    {
        if (!Available || movePuppet == null) return;
        try { movePuppet.InvokeAction(objectIndex, x, y, z, rot); }
        catch (Exception ex) { log.Debug("[HMSync] HDM MovePuppet failed: " + ex.Message); }
    }

    public void DespawnPuppet(int objectIndex)
    {
        if (!Available || despawnPuppet == null) return;
        try { despawnPuppet.InvokeAction(objectIndex); }
        catch (Exception ex) { log.Debug("[HMSync] HDM DespawnPuppet failed: " + ex.Message); }
    }

    public void Dispose()
    {
        try { evDisguiseChanged?.Unsubscribe(OnDisguiseChangedRaw); } catch { }
        try { evActionFired?.Unsubscribe(OnActionFiredRaw); } catch { }
        try { evPuppetSpawned?.Unsubscribe(OnPuppetSpawnedRaw); } catch { }
        try { evPuppetReady?.Unsubscribe(OnPuppetReadyRaw); } catch { }
        try { evPuppetDespawned?.Unsubscribe(OnPuppetDespawnedRaw); } catch { }
        try { evPuppetMoved?.Unsubscribe(OnPuppetMovedRaw); } catch { }
        try { evOwnBodyHidden?.Unsubscribe(OnOwnBodyHiddenRaw); } catch { }
        try { evFreezeChanged?.Unsubscribe(OnFreezeChangedRaw); } catch { }
        try { onReady?.Unsubscribe(OnHdmReadyRaw); } catch { }
        try { onDisposing?.Unsubscribe(OnHdmDisposingRaw); } catch { }
    }
}
