namespace HMSync.Services;

using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

// HMS ↔ Moniker integration (S328x). Moniker (https://github.com/…/Moniker) is a nameplate-name plugin with a
// sync-friendly IPC surface (namespace "Moniker.*"), mirroring the Mare/Honorific courier pattern: a courier reads a
// player's chosen name and applies it to another client's copy of that player. HMS is that courier for a session.
//
// DETECTION: the IPC namespace IS the stable signature - no opcode, no memory scan, no drift. We probe
// "Moniker.ApiVersion"; if it resolves and returns a compatible major version, Moniker is present. This is exactly how
// HMS detects Penumbra/Glamourer. Absence is soft: every call is guarded, so no Moniker = the feature is simply inert.
//
// FLOW:
//   - Local (broadcast): GetLocalName() reads the host's own chosen name (empty if none/disabled) → HMS puts it in the
//     transform stream.
//   - Remote (apply): ApplyName(objectIndex, name, hideFc, hideName) calls Moniker.SetCharacterName on a peer's puppet so the
//     peer renders the chosen name. ClearName removes it (on teardown / when the sender clears theirs).
public sealed class MonikerService : IDisposable
{
    private const uint RequiredMajor = 2;   // Moniker IpcProvider.MajorVersion

    private readonly IPluginLog log;
    private readonly IDalamudPluginInterface pi;

    private ICallGateSubscriber<(uint, uint)>? apiVersion;
    private ICallGateSubscriber<string>? getLocalName;
    private ICallGateSubscriber<int, string, object>? setName;
    private ICallGateSubscriber<int, object>? clearName;
    private ICallGateSubscriber<object>? onReady;       // Moniker.Ready
    private ICallGateSubscriber<object>? onDisposing;   // Moniker.Disposing

    public bool Available { get; private set; }

    // S328z: the local player's current object index, supplied by the plugin. HMS applies names to PEER puppets only;
    // this guard ensures a stale/re-indexed peer ObjectIndex can never make HMS Set/Clear a name on the HOST's OWN
    // object (which would look like a "cached" wrong name Moniker then owns by the host's EntityId). Refuse any
    // apply/clear whose index equals the local player's.
    public Func<int>? LocalPlayerIndex;

    private bool IsLocalIndex(int objectIndex)
    {
        try { return LocalPlayerIndex != null && LocalPlayerIndex() == objectIndex; }
        catch { return false; }
    }

    // Mirror of Moniker's carried payload (name + hide-FC + hide-name + hide-title flags). HideName is additive
    // (Moniker IPC 2.2), HideTitle additive (Moniker IPC 2.3): JSON from an older sender lacks it → defaults false;
    // older receivers ignore it. Must stay a superset of what we both read and write, or the flag is silently dropped
    // in transit (which is exactly the bug this fixes).
    private sealed class NameData { public string Name = ""; public bool HideFcTag; public bool HideName; public bool HideTitle; }

    public MonikerService(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.pi = pi;
        this.log = log;
        TryConnect();

        // Re-probe when Moniker announces itself (installed/enabled after us) or goes away.
        try
        {
            onReady = pi.GetIpcSubscriber<object>("Moniker.Ready");
            onReady.Subscribe(OnMonikerReady);
            onDisposing = pi.GetIpcSubscriber<object>("Moniker.Disposing");
            onDisposing.Subscribe(OnMonikerDisposing);
        }
        catch (Exception ex) { log.Debug("[HMSync] Moniker ready/disposing subscribe failed: " + ex.Message); }
    }

    private void OnMonikerReady() { TryConnect(); if (Available) log.Information("[HMSync] Moniker detected - nameplate sync active."); }
    private void OnMonikerDisposing() { Available = false; log.Information("[HMSync] Moniker went away - nameplate sync inert."); }

    private void TryConnect()
    {
        try
        {
            apiVersion = pi.GetIpcSubscriber<(uint, uint)>("Moniker.ApiVersion");
            var (major, _) = apiVersion.InvokeFunc();
            if (major != RequiredMajor)
            {
                Available = false;
                log.Information($"[HMSync] Moniker present but API v{major} != required v{RequiredMajor} - nameplate sync off.");
                return;
            }
            getLocalName = pi.GetIpcSubscriber<string>("Moniker.GetLocalCharacterName");
            setName = pi.GetIpcSubscriber<int, string, object>("Moniker.SetCharacterName");
            clearName = pi.GetIpcSubscriber<int, object>("Moniker.ClearCharacterName");
            Available = true;
        }
        catch
        {
            // Not installed / not ready yet → inert. Not an error (matches how optional integrations degrade).
            Available = false;
        }
    }

    // The host's own chosen nameplate name (empty if none set or Moniker disabled). Broadcast into the transform.
    public (string name, bool hideFc, bool hideName, bool hideTitle) GetLocalName()
    {
        if (!Available || getLocalName == null) return ("", false, false, false);
        try
        {
            var json = getLocalName.InvokeFunc();
            if (string.IsNullOrEmpty(json)) return ("", false, false, false);
            var data = JsonConvert.DeserializeObject<NameData>(json);
            return data == null ? ("", false, false, false) : (data.Name ?? "", data.HideFcTag, data.HideName, data.HideTitle);
        }
        catch (Exception ex) { log.Debug("[HMSync] Moniker GetLocalName failed: " + ex.Message); return ("", false, false, false); }
    }

    // Apply a chosen name to a peer's puppet (by object index) so this client renders it. Empty name clears.
    // NOTE: the game caches the rendered nameplate - calling SetCharacterName again with a NEW name updates Moniker's
    // data but doesn't always invalidate the already-drawn nameplate (observed: first set redraws, subsequent changes
    // don't). So on a re-set we CLEAR first, then set, forcing a full invalidate→redraw cycle (the pattern nameplate
    // plugins like Honorific use). forceRedraw is passed by the apply layer when the name is CHANGING (not initial).
    public void ApplyName(int objectIndex, string name, bool hideFc, bool hideName, bool hideTitle, bool forceRedraw = false)
    {
        if (!Available) return;
        if (IsLocalIndex(objectIndex)) return;   // never touch the host's own nameplate
        try
        {
            // A hide-name payload carries an EMPTY name on purpose (the plate is blanked), so it must NOT be mistaken
            // for a clear: only clear when there is genuinely nothing to apply (no name AND neither hide flag set).
            if (string.IsNullOrEmpty(name) && !hideFc && !hideName && !hideTitle) { clearName?.InvokeAction(objectIndex); return; }
            // v0.7.369: a plain set is sufficient. Moniker's IPC handlers now call RequestNameplateRedraw() themselves
            // (its Set/Clear previously mutated the peer-name dictionary without dirtying the plate, so a peer's plate
            // waited for the next ORGANIC rebuild - the "flag change doesn't repaint, name change does" bug). HMS no
            // longer clears-then-sets or defers across frames to force it; forceRedraw is retained in the signature but
            // is now a no-op hint, since redraws coalesce per frame on Moniker's side.
            var json = JsonConvert.SerializeObject(new NameData { Name = name, HideFcTag = hideFc, HideName = hideName, HideTitle = hideTitle });
            setName?.InvokeAction(objectIndex, json);
        }
        catch (Exception ex) { log.Debug("[HMSync] Moniker ApplyName failed: " + ex.Message); }
    }

    public void ClearName(int objectIndex)
    {
        if (!Available) return;
        if (IsLocalIndex(objectIndex)) return;   // never touch the host's own nameplate
        try { clearName?.InvokeAction(objectIndex); }
        catch (Exception ex) { log.Debug("[HMSync] Moniker ClearName failed: " + ex.Message); }
    }

    public void Dispose()
    {
        try { onReady?.Unsubscribe(OnMonikerReady); } catch { }
        try { onDisposing?.Unsubscribe(OnMonikerDisposing); } catch { }
    }
}
