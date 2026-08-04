using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace HMSync.Services;

/// <summary>
/// Manages visibility of player characters during HM-Sync sessions.
/// 
/// Uses RenderFlags instead of DisableDraw/EnableDraw. DisableDraw destroys
/// the DrawObject, which triggers Penumbra's Create CharacterBase hook in a
/// rebuild loop (Penumbra sees a character with no draw object and recreates
/// it every 500ms). RenderFlags hides the character while keeping the draw
/// object intact - Penumbra has nothing to rebuild.
/// 
/// RenderFlags == 0x00 means visible. Setting bit 1 (0x02) hides the
/// character, nameplate, and selection indicator. Same approach used by
/// VoidList and similar visibility plugins.
/// </summary>
public unsafe class ActorVisibilityService : IDisposable
{
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private const uint InvisibleFlag = 0x02;

    // Helper to avoid casting everywhere
    private static void Hide(GameObject* native) => native->RenderFlags |= (VisibilityFlags)InvisibleFlag;
    private static void Show(GameObject* native) => native->RenderFlags &= ~(VisibilityFlags)InvisibleFlag;
    private static bool IsHidden(GameObject* native) => (native->RenderFlags & (VisibilityFlags)InvisibleFlag) != 0;

    private bool active;

    // Object indices we've hidden - so we can restore them on Stop
    private readonly HashSet<ushort> hiddenIndices = new();

    // Object indices of registered peers - these should be visible
    private readonly HashSet<ushort> peerIndices = new();

    // D-16.2/.3: entity ids of pass-through arrivals to hide the moment their actor exists. The packet filter passes a
    // real walk-in's PlayerSpawn through INBOUND (so the game instantiates it) and fires the arrival event on the
    // NETWORK thread; this queue is the thread-safe hand-off. Drained at the TOP of Update (framework thread) so the
    // newcomer is hidden on the very next frame - before the throttled sweep would otherwise catch it up to ~0.5s later,
    // which would flash the real room into the virtual scene. Enqueue is the only thing that touches this off-thread.
    private readonly System.Collections.Concurrent.ConcurrentQueue<uint> pendingHideEntityIds = new();
    public void QueueImmediateHide(uint entityId) { if (entityId != 0) pendingHideEntityIds.Enqueue(entityId); }
    // Framework-thread retry window (eid → frames remaining). The game may register the passed-through actor a frame or
    // two after Original returns, so a single-frame drain can miss it. Retrying ~1s also gives the diagnostic a fair
    // chance to observe the actor before declaring it "never instantiated".
    private readonly Dictionary<uint, int> hideRetry = new();
    private const int HideRetryFrames = 60;

    // D-16 b29: post-bind "materialize" watch. b28 proved the late-join BIND works (ContentId → OnPeerBound →
    // RegisterPeer cleared our 0x02: rf 0xC02 → 0xC00) and HMS drives the peer - yet they stay a ghost, because the
    // game's OWN render-gate bits (0xC00 = 0x400|0x800) were set at pass-through spawn (the actor was instantiated into
    // the REAL-zone render context after the virtual hop) and we only ever manage 0x02. A correctly-homed peer sits at
    // rf=0x00. So for a few seconds after bind we (a) force RenderFlags to 0 each frame (the visible-peer target state,
    // low risk) and (b) log rf + whether DrawObject is null. If forcing 0 makes them draw → the flags were the gate,
    // done. If they stay a ghost with DrawObject null → the actor has no draw object in this scene and needs a Penumbra
    // redraw/rebuild, not a flag flip - pivot there. Change-gated so the log is a clean transition trace.
    private readonly Dictionary<ushort, int> materializeWatch = new();
    private readonly Dictionary<ushort, string> materializePrev = new();
    private const int MaterializeFrames = 180;

    public ActorVisibilityService(IObjectTable objectTable, IFramework framework, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.framework = framework;
        this.log = log;
    }

    // v0.7.360: set by the plugin so the hide sweep can stand down during gpose. See the guard in Update() -
    // gpose creates COPIES of actors at different object indices, so the `idx == localIdx` guard doesn't protect
    // the player's own gpose copy and the sweep was hiding it (taking its mount with it).
    public Func<bool>? IsGPosing;

    /// <summary>
    /// Begin hiding non-self, non-peer players.
    /// </summary>
    public void Start()
    {
        if (active) return;
        active = true;
        hiddenIndices.Clear();
        peerIndices.Clear();
        hideRetry.Clear();
        materializeWatch.Clear();
        materializePrev.Clear();
        while (pendingHideEntityIds.TryDequeue(out _)) { }

        HideAll();
        log.Information("[HMSync] ActorVisibility started - hiding " + hiddenIndices.Count + " players");
    }

    public void Stop()
    {
        if (!active) return;
        active = false;

        foreach (var idx in hiddenIndices)
        {
            var obj = objectTable[(int)idx];
            if (obj == null) continue;
            var native = (GameObject*)obj.Address;
            Show(native);
        }

        hiddenIndices.Clear();
        peerIndices.Clear();
        hideRetry.Clear();
        materializeWatch.Clear();
        materializePrev.Clear();
        while (pendingHideEntityIds.TryDequeue(out _)) { }
        log.Information("[HMSync] ActorVisibility stopped - all players restored");
    }

    /// <summary>
    /// Register a peer as visible. If they were hidden, restore their RenderFlags.
    /// </summary>
    public void RegisterPeer(ushort objectIndex)
    {
        peerIndices.Add(objectIndex);
        hiddenIndices.Remove(objectIndex);

        // v0.7.335: ALWAYS clear the hide bit on bind - not gated behind our own hiddenIndices. A late joiner was hidden
        // by ZoneLoadService's LOAD-TIME sweep (a different tracking set), so it was never in our hiddenIndices; the old
        // gated Show() then no-op'd and they stayed invisible on an existing member. Clearing unconditionally is safe
        // (a peer must be visible) and fixes the late-join case; the ZoneLoad set is cleared separately via the plugin.
        var obj = objectTable[(int)objectIndex];
        if (obj != null)
        {
            var native = (GameObject*)obj.Address;
            uint before = (uint)native->RenderFlags;
            Show(native);
            // DIAG (b28): the reveal end of the late-join arc. Pairs with [ROSTER-PASS]/[ROSTER-HIDE]: a mid-session
            // walk-in shows as hide (rf gains 0x02), then this fires the instant their ContentId binds to the passed-
            // through actor, clearing 0x02 again. Seeing this line with rf 0x…2 → 0x…0 on the same idx that was hidden
            // is the whole machine proven end-to-end. If it never fires, the peer-state/bind side (relay → PeerInfo →
            // resolve loop) never matched - a session/relay problem, not visibility.
            log.Debug("[HMSync] [ROSTER-BIND] peer idx=" + objectIndex + " " + obj.Name +
                " rf 0x" + before.ToString("X") + " → 0x" + ((uint)native->RenderFlags).ToString("X") + " (revealed)");
            // b29: start the materialize watch - the reveal cleared our 0x02 but the game's own 0xC00 gate may remain.
            materializeWatch[objectIndex] = MaterializeFrames;
            materializePrev.Remove(objectIndex);
        }
        else
        {
            log.Debug("[HMSync] [ROSTER-BIND] peer idx=" + objectIndex + " bound but object slot is null - cannot reveal yet");
        }
    }

    /// <summary>
    /// Unregister a peer. Hide them again.
    /// </summary>
    public void UnregisterPeer(ushort objectIndex)
    {
        peerIndices.Remove(objectIndex);

        var obj = objectTable[(int)objectIndex];
        if (obj != null)
        {
            var native = (GameObject*)obj.Address;
            Hide(native);
            hiddenIndices.Add(objectIndex);
            log.Information("[HMSync] Peer [" + objectIndex + "] " + obj.Name + " - hidden (left)");
        }
    }

    /// <summary>
    /// Re-run hiding pass. Call after zone load.
    /// </summary>
    public void Refresh()
    {
        if (!active) return;
        HideAll();
    }

    /// <summary>
    /// Continuous maintenance. Throttled to twice per second.
    /// Catches objects where the game or other plugins cleared our RenderFlags.
    /// </summary>
    private int hideThrottle;

    // ── v0.7.390: gpose transition audit ───────────────────────────────────────────────────────
    // Two symptoms sit on the gpose boundary and neither is measured yet:
    //   ENTRY - peers appear TWICE. Suspected: gpose builds copies at new indices, and since v0.7.360
    //           stands this sweep down while gposing, a peer's clone is no longer hidden. INFERRED,
    //           not observed.
    //   EXIT  - your own character goes invisible locally (peers still see you). Mechanism UNKNOWN.
    //           The "stale localIdx" theory is falsified: localIdx is re-read fresh every sweep below.
    //
    // Everything in this service is keyed on OBJECT INDEX - hiddenIndices, peerIndices, idx == localIdx -
    // and gpose reshapes the object table. Manual §2.4: identity is ContentId, the object handle is
    // ephemeral. This audit logs both keys side by side so we can see exactly where they diverge:
    //   • two indices sharing one ContentId  → that is the clone, and which of them carries 0x02
    //   • localIdx not matching the index whose ContentId is ours → the self-hide, caught in the act
    // Runs EVERY frame, before the throttle and before the gpose stand-down, change-gated so the log
    // reads as a clean transition trace rather than spam.
    public bool Diag { get; set; }
    private string prevDiagLine = "";

    private void DiagTick()
    {
        var lp = objectTable.LocalPlayer;
        ulong myCid = 0;
        int realLocalIdx = -1;
        if (lp != null)
        {
            var lch = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)lp.Address;
            if (lch != null) myCid = lch->ContentId;
            realLocalIdx = lp.ObjectIndex;
        }

        var sb = new System.Text.StringBuilder();
        var cidCount = new Dictionary<ulong, int>();
        foreach (var obj in objectTable)
        {
            if (obj == null) continue;
            var go = (GameObject*)obj.Address;
            if (go == null || go->ObjectKind != ObjectKind.Pc) continue;
            var idx = (ushort)obj.ObjectIndex;
            var ch = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)obj.Address;
            ulong cid = ch != null ? ch->ContentId : 0;
            if (cid != 0) cidCount[cid] = cidCount.GetValueOrDefault(cid) + 1;

            sb.Append(" [").Append(idx).Append(']')
              .Append(cid != 0 && cid == myCid ? "SELF" : peerIndices.Contains(idx) ? "peer" : "othr")
              .Append(" cid=").Append(cid.ToString("X"))
              .Append(" rf=0x").Append(((uint)go->RenderFlags).ToString("X"))
              .Append(IsHidden(go) ? "*HIDDEN*" : "")
              .Append(hiddenIndices.Contains(idx) ? "(tracked)" : "");
        }

        int dupes = 0;
        foreach (var kv in cidCount) if (kv.Value > 1) dupes++;

        var line = "gpose=" + (IsGPosing?.Invoke() == true) +
            " localIdx=" + realLocalIdx + " myCid=" + myCid.ToString("X") +
            " active=" + active +
            " peers=[" + string.Join(",", peerIndices) + "]" +
            " hidden=[" + string.Join(",", hiddenIndices) + "]" +
            (dupes > 0 ? "  <<< " + dupes + " ContentId(s) AT TWO INDICES - THE CLONE" : "") +
            sb;

        if (line == prevDiagLine) return;   // change-gated
        prevDiagLine = line;
        log.Information("[HMSync] [GPOSEDIAG] " + line);
    }

    // v0.7.391: did WE hide this index? GPoseMountDrawService's recovery pass needs this - it was
    // blanket-clearing 0x02 from every Pc in gpose, which undid the game's own hide of the originals
    // when it spawned clones, so both copies drew. Only ever un-hide a bit we set.
    public bool WasHiddenByUs(ushort objectIndex) => hiddenIndices.Contains(objectIndex);

    public void Update()
    {
        if (Diag) DiagTick();   // ahead of every guard - the transition is the thing we need to see
        if (!active) return;

        // D-16.2/.3: hide pass-through arrivals immediately. Drain BEFORE the gpose stand-down and the throttle gate so a
        // freshly-instantiated non-member is hidden within a frame or two (not up to the throttle later). Resolve the
        // ephemeral entity id → object on the framework thread; a bound session member (in peerIndices) is left visible.
        // Retried across HideRetryFrames because the game may register the actor a couple of frames after Original.
        while (pendingHideEntityIds.TryDequeue(out var qeid))
            hideRetry[qeid] = HideRetryFrames;
        if (hideRetry.Count > 0)
        {
            foreach (var eid in new List<uint>(hideRetry.Keys))
            {
                bool resolved = false;
                foreach (var obj in objectTable)
                {
                    if (obj == null || obj.EntityId != eid) continue;
                    resolved = true;
                    var idx = (ushort)obj.ObjectIndex;
                    var native = (GameObject*)obj.Address;
                    var ch = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)obj.Address;
                    ulong cid = ch != null ? ch->ContentId : 0;
                    if (peerIndices.Contains(idx))
                    {
                        log.Debug("[HMSync] [ROSTER-HIDE] eid=0x" + eid.ToString("X8") + " idx=" + idx +
                            " is a bound PEER - leaving visible");
                        break;
                    }
                    bool isPc = native->ObjectKind == ObjectKind.Pc;
                    // b27 proved this fires with a real index/kind and a populated ContentId - i.e. pass-through
                    // instantiates a bindable actor. Kept at Debug now the machine is validated (see WORKING-CHANGELOG).
                    log.Debug("[HMSync] [ROSTER-HIDE] eid=0x" + eid.ToString("X8") + " idx=" + idx +
                        " kind=" + native->ObjectKind + " cid=" + (cid != 0 ? "set" : "0") +
                        " rf=0x" + ((uint)native->RenderFlags).ToString("X") + (isPc ? " → HIDE" : " (non-Pc, skip)"));
                    if (isPc) { Hide(native); hiddenIndices.Add(idx); }
                    break;
                }
                if (resolved) { hideRetry.Remove(eid); continue; }
                if (--hideRetry[eid] <= 0)
                {
                    hideRetry.Remove(eid);
                    log.Information("[HMSync] [ROSTER-HIDE] eid=0x" + eid.ToString("X8") +
                        " NOT FOUND in object table after " + HideRetryFrames + " frames - pass-through did not instantiate a usable actor.");
                }
            }
        }

        // b29: materialize watch - for a few seconds after a late-join bind, force the peer's RenderFlags to the
        // visible-peer target (0x00) and log the game's own gate bits + DrawObject presence. See the field comment.
        if (materializeWatch.Count > 0)
        {
            foreach (var idx in new List<ushort>(materializeWatch.Keys))
            {
                var obj = objectTable[(int)idx];
                if (obj == null)
                {
                    if (--materializeWatch[idx] <= 0) { materializeWatch.Remove(idx); materializePrev.Remove(idx); }
                    continue;
                }
                var native = (GameObject*)obj.Address;
                uint rf = (uint)native->RenderFlags;
                bool drawNull = native->DrawObject == null;
                string line = "rf=0x" + rf.ToString("X") + " draw=" + (drawNull ? "NULL" : "ok");
                if (!materializePrev.TryGetValue(idx, out var prev) || prev != line)
                {
                    materializePrev[idx] = line;
                    log.Debug("[HMSync] [ROSTER-MAT] idx=" + idx + " " + obj.Name + " " + line +
                        (rf != 0 ? " → forcing rf=0" : " (already visible)"));
                }
                // Force the visible-peer target state. A correctly-homed peer sits at 0x00; if the game re-asserts
                // 0xC00 next frame the change-gated log will show it flapping (→ redraw needed).
                if (rf != 0) native->RenderFlags = (VisibilityFlags)0;
                if (--materializeWatch[idx] <= 0) { materializeWatch.Remove(idx); materializePrev.Remove(idx); }
            }
        }

        // v0.7.360 ROOT FIX for "HMS mounts disappear in gpose". GPose builds COPIES of actors at DIFFERENT object
        // indices. The `idx == localIdx` guard below only protects the LIVE local player, so the player's own gpose
        // copy (still ObjectKind.Pc, not in peerIndices) fell through and got RenderFlags |= 0x02 - and the mount
        // attached to it inherited the hide. The probe caught it exactly: at the first sweep after gpose entry
        // (frame 36 - this 30-frame throttle), RIDER rf 0x0 → 0x1002 and MOUNT rf 0x0 → 0x8802 on the SAME frame,
        // i.e. both gained bit 0x02. Nothing was destroying the mount; it was being hidden.
        //
        // Standing the sweep down while gposing is the correct behaviour, not just a patch: the sweep exists to hide
        // OTHER real players from a synthetic session, and gpose is a local cinematic mode where the actor set is
        // gpose's own to manage. Anything hidden before entry stays hidden (we simply stop re-sweeping), and Revert
        // still restores every flag we ever set.
        if (IsGPosing?.Invoke() == true) return;

        hideThrottle++;
        if (hideThrottle < 30) return;
        hideThrottle = 0;

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null) return;
        var localIdx = (ushort)localPlayer.ObjectIndex;

        foreach (var obj in objectTable)
        {
            var idx = (ushort)obj.ObjectIndex;
            if (idx == localIdx) continue;
            if (peerIndices.Contains(idx)) continue;

            var native = (GameObject*)obj.Address;
            if (native->ObjectKind != ObjectKind.Pc) continue;

            if (!IsHidden(native))
            {
                Hide(native);
                hiddenIndices.Add(idx);
            }
        }
    }

    /// <summary>
    /// Hide all non-self, non-peer player objects via RenderFlags.
    /// </summary>
    private void HideAll()
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null) return;

        var localIdx = (ushort)localPlayer.ObjectIndex;

        foreach (var obj in objectTable)
        {
            var idx = (ushort)obj.ObjectIndex;
            if (idx == localIdx) continue;
            if (peerIndices.Contains(idx)) continue;

            var native = (GameObject*)obj.Address;
            if (native->ObjectKind != ObjectKind.Pc) continue;

            Hide(native);
            hiddenIndices.Add(idx);
            log.Debug("[HMSync] Hidden [" + idx + "] " + obj.Name + " via RenderFlags");
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
