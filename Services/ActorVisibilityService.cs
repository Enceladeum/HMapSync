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
            Show(native);
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
