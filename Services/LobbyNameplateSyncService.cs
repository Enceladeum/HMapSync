namespace HMSync.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using HMSync.Sync;
using HMSync.Wire;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
// LobbyNameplateSyncService — sync Moniker nameplates in the LOBBY (connected + room-joined, no synthetic map).
//
// WHY THIS EXISTS. The Moniker courier normally rides the Cold transform lane (ColdPayload MonikerName/…), which
// only runs INSIDE a synthetic session (stateCapture/stateApply are Start()ed in EngageSyntheticSession). So while
// peers sit together in the lobby — connected, room-joined, but before anyone loads a map — nothing carries the
// chosen name, and everyone sees each other's real character name. This service fills that gap with a dedicated
// relay-opaque lane (WireKind.LobbyNameplate 0x54, in the 0x50–0x5F family → RMS fans it out verbatim, no relay
// change, old clients ignore it), gated behind config.SyncLobbyNameplates (ON by default as of b198, so the lobby
// mirrors in-session nameplate sync without a hidden opt-in; user can still untick it). It follows the b193
// OwnBodyHidden dedicated-lane precedent.
//
// SCOPE. Lobby ONLY. The instant a map loads (inLoadedMap → true) this service reverts everything it applied and
// hands the nameplate concern back to the Cold-lane courier, so the two never fight. Symmetric on disconnect /
// toggle-off.
//
// DRIVE MODEL. Everything runs from Tick(), called each frame from the plugin's always-on OnFrameworkUpdate (the
// Cold/Warm apply loops don't run in the lobby, so a self-contained tick is the reliable driver here). Tick both
// BROADCASTS our local name (on change / on peer-join re-offer) and APPLIES cached remote names once each source's
// body binds. relay.OnLobbyNameplateReceived fires on the RECEIVE thread → we only stash into a concurrent cache
// there; all object-table / Moniker work happens on the framework thread inside Tick.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
public sealed class LobbyNameplateSyncService : IDisposable
{
    private readonly RelaySyncService relay;
    private readonly StateApplyService stateApply;
    private readonly MonikerService moniker;
    private readonly HMSyncConfig config;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Func<ulong> localContentId;
    private readonly Func<bool> inLoadedMap;

    // Latest nameplate per remote source, keyed by SenderContentId. Written on the receive thread, read/applied on
    // the framework thread in Tick. Concurrent because of that thread crossing.
    private readonly ConcurrentDictionary<ulong, LobbyNameplatePayload> remoteNames = new();

    // What we've currently applied per local object index (composite key), so we only re-apply on change and can
    // revert precisely on deactivate. Framework-thread only.
    private readonly Dictionary<ushort, string> appliedNames = new();

    // Our own ContentId, cached for receive-thread echo-suppression without an off-thread object-table read.
    private ulong cachedSelfCid;

    // Last local nameplate we broadcast (composite key). "" sentinel = "nothing broadcast yet / force re-offer".
    private string lastLocalBroadcast = " force";
    private bool wasActive;

    // b196 DIAGNOSTIC (change-gated Information logging — demote to Debug once the b195 sync bug is understood).
    // lastGateReason: only log gate state on transition (active / inactive-with-reason), never per frame.
    // loggedUnbound: log "have a name for this source but its body isn't bound yet" at most once per source.
    private string lastGateReason = "";
    private readonly HashSet<ulong> loggedUnbound = new();

    public LobbyNameplateSyncService(RelaySyncService relay, StateApplyService stateApply, MonikerService moniker,
        HMSyncConfig config, IFramework framework, IPluginLog log, Func<ulong> localContentId, Func<bool> inLoadedMap)
    {
        this.relay = relay;
        this.stateApply = stateApply;
        this.moniker = moniker;
        this.config = config;
        this.framework = framework;
        this.log = log;
        this.localContentId = localContentId;
        this.inLoadedMap = inLoadedMap;

        relay.OnLobbyNameplateReceived += OnReceived;
    }

    private static string Composite(string name, bool hideFc, bool hideName, bool hideTitle, bool hideStatus)
        => name + "|" + (hideFc ? 1 : 0) + (hideName ? 1 : 0) + (hideTitle ? 1 : 0) + (hideStatus ? 1 : 0);

    // RECEIVE thread: just cache the latest per source. All apply happens on the framework thread in Tick.
    private void OnReceived(LobbyNameplatePayload p)
    {
        if (p == null) return;
        if (cachedSelfCid != 0 && p.SenderContentId == cachedSelfCid) return;   // never mirror our own broadcast
        remoteNames[p.SenderContentId] = p;
        loggedUnbound.Remove(p.SenderContentId);   // fresh payload → allow one more "unbound" log if it's still unbound
        log.Debug($"[LobbyNameplate] RX name='{p.MonikerName}' from cid={p.SenderContentId} (cachedSelfCid={cachedSelfCid})");
    }

    // Force our next Tick to (re-)broadcast the local name — used on peer-join so a late-joiner catches up (the lane
    // is change-gated, so without this our name set before they joined would be invisible to them).
    public void RequestRebroadcast() => lastLocalBroadcast = " force";

    // A peer left: drop their cached name. Their applied nameplate is cleared by the plugin's OnPeerLeft path
    // (moniker.ClearName), so we only forget the cache here.
    public void OnPeerDeparted(ulong contentId)
    {
        remoteNames.TryRemove(contentId, out _);
    }

    // Called each frame from the always-on framework loop.
    public void Tick()
    {
        bool active = config.SyncLobbyNameplates
                      && moniker.Available
                      && relay.IsConnected
                      && relay.RoomJoinedAcknowledged
                      && !inLoadedMap();   // lobby ONLY — the Cold-lane courier owns nameplates inside a loaded map

        if (!active)
        {
            if (wasActive) Deactivate();
            // DIAGNOSTIC: log WHICH gate condition is holding us inactive, once per reason change.
            string reason = !config.SyncLobbyNameplates ? "toggle-off"
                : !moniker.Available ? "moniker-unavailable"
                : !relay.IsConnected ? "not-connected"
                : !relay.RoomJoinedAcknowledged ? "room-not-joined"
                : "in-loaded-map";
            if (reason != lastGateReason)
            {
                lastGateReason = reason;
                log.Debug($"[LobbyNameplate] inactive: {reason}");
            }
            return;
        }

        if (lastGateReason != "active")
        {
            lastGateReason = "active";
            log.Debug("[LobbyNameplate] active (lobby gate open)");
        }

        if (!wasActive)
        {
            wasActive = true;
            lastLocalBroadcast = " force";   // fresh entry into the lobby → (re-)offer our name
        }

        // SENDER: broadcast our local name on change (or when a re-offer was requested).
        cachedSelfCid = localContentId();
        var (name, hideFc, hideName, hideTitle, hideStatus) = moniker.GetLocalName();
        string key = Composite(name, hideFc, hideName, hideTitle, hideStatus);
        if (key != lastLocalBroadcast)
        {
            lastLocalBroadcast = key;
            log.Debug($"[LobbyNameplate] TX name='{name}' cid={cachedSelfCid} (hideFc={hideFc},hideName={hideName},hideTitle={hideTitle},hideStatus={hideStatus})");
            _ = relay.SendLobbyNameplate(new LobbyNameplatePayload
            {
                SubjectId = "",
                SenderContentId = cachedSelfCid,
                MonikerName = name,
                MonikerHideFc = hideFc,
                MonikerHideName = hideName,
                MonikerHideTitle = hideTitle,
                MonikerHideStatus = hideStatus,
            });
        }

        // RECEIVER: apply cached remote names to bound peers. GetPeerObjectIndices() forces a bind attempt for any
        // co-located peer that registered in the lobby but isn't resolved yet, so a name applies as soon as the body
        // is in render range.
        if (remoteNames.IsEmpty) return;
        stateApply.GetPeerObjectIndices();   // side effect: resolve lobby-registered peers' object indices
        foreach (var info in stateApply.Peers.Values)
        {
            if (info.ContentId == 0) continue;
            if (!remoteNames.TryGetValue(info.ContentId, out var p)) continue;   // no cached name for this source

            if (!info.ObjectIndex.HasValue)
            {
                // We have a name to show but the peer's body hasn't resolved to an object index yet. Log once
                // per source so we can tell "binding never happens in the lobby" apart from "apply silently failed".
                if (loggedUnbound.Add(info.ContentId))
                    log.Debug($"[LobbyNameplate] have name='{p.MonikerName}' for cid={info.ContentId} but ObjectIndex NOT bound");
                continue;
            }
            loggedUnbound.Remove(info.ContentId);

            ushort idx = info.ObjectIndex.Value;
            string ck = Composite(p.MonikerName, p.MonikerHideFc, p.MonikerHideName, p.MonikerHideTitle, p.MonikerHideStatus);
            if (appliedNames.TryGetValue(idx, out var prev) && prev == ck) continue;   // already applied this exact name

            log.Debug($"[LobbyNameplate] APPLY name='{p.MonikerName}' idx={idx} cid={info.ContentId} forceRedraw={appliedNames.ContainsKey(idx)}");
            moniker.ApplyName(idx, p.MonikerName, p.MonikerHideFc, p.MonikerHideName, p.MonikerHideTitle, p.MonikerHideStatus,
                forceRedraw: appliedNames.ContainsKey(idx));
            appliedNames[idx] = ck;
        }
    }

    // Revert everything we applied and stop — on toggle-off / disconnect / map-load (hand back to the Cold lane).
    private void Deactivate()
    {
        foreach (var idx in appliedNames.Keys)
        {
            try { moniker.ClearName(idx); } catch { }
        }
        appliedNames.Clear();
        wasActive = false;
        lastLocalBroadcast = " force";
    }

    // Full teardown: revert applied names + forget all caches. Called on session reset.
    public void Reset()
    {
        Deactivate();
        remoteNames.Clear();
        loggedUnbound.Clear();
    }

    public void Dispose()
    {
        try { relay.OnLobbyNameplateReceived -= OnReceived; } catch { }
        try { Deactivate(); } catch { }
    }
}
