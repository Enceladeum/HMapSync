using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace HMSync.Services;

/// <summary>
/// S328ai: broad receiver-side locomotion/animation diagnostic. When armed (/hms locodiag), logs per-peer, per-frame,
/// the animation-resolver's inputs and decisions - so we can read the exact frame-by-frame story of a jump, a
/// dismount, a stop, a turn, etc., and tell whether a misbehavior is SENDER over-reporting a phase across ticks or
/// the RECEIVER over-sustaining a single report across frames. Built to catch the jump-land / dismount-settle
/// double-loop, but deliberately broad so any resolver misbehavior (not just jumps) shows up.
///
/// Logging is EDGE-BASED to keep the log readable: it prints a line only when something CHANGES for a peer
/// (jump phase, move state, mount id, the resolved target timeline, or a PlayTimeline fire) - plus a per-phase
/// CONSECUTIVE-FRAME COUNT, which is the key number for the double-loop question (how many frames did the receiver
/// see JumpLanding, and how many times did it (re)play the land clip).
///
/// Zero cost when not armed (a single bool check at each hook). Auto-disarms after the window.
/// </summary>
public sealed class LocoDiagService
{
    private readonly IPluginLog log;
    public LocoDiagService(IPluginLog log) { this.log = log; }

    private bool armed;
    private DateTime endUtc;

    // Per-peer last-seen values, so we log transitions rather than every frame.
    private sealed class PeerTrace
    {
        public byte LastJumpPhase = 255;
        public byte LastMoveState = 255;
        public ushort LastMountId = 0xFFFF;
        public ushort LastTarget = 0xFFFF;      // last RESOLVED target timeline
        public int PhaseFrameCount;             // consecutive frames in the current jump phase
        public int TargetFrameCount;            // consecutive frames the current target has been sustained
        public int PlayFiresThisTarget;         // how many PlayTimeline fires for the current target (should be 1)
        public bool Init;
    }
    private readonly Dictionary<string, PeerTrace> traces = new();

    public void Start(int ms = 15000)
    {
        armed = true;
        endUtc = DateTime.UtcNow.AddMilliseconds(ms);
        traces.Clear();
        log.Information("[HMSync] [LOCODIAG] === armed for " + (ms / 1000) + "s === logs per-peer resolver transitions " +
            "(jumpPhase/moveState/mountId/target + consecutive-frame counts + PlayTimeline fires).");
    }

    public bool Armed
    {
        get
        {
            if (armed && DateTime.UtcNow >= endUtc)
            {
                armed = false;
                log.Information("[HMSync] [LOCODIAG] === window ended ===");
            }
            return armed;
        }
    }

    private PeerTrace TraceFor(string peer)
    {
        if (!traces.TryGetValue(peer, out var t)) { t = new PeerTrace(); traces[peer] = t; }
        return t;
    }

    /// <summary>Called each frame in the resolver, BEFORE the write, with the resolved target. Logs transitions.</summary>
    public void OnResolve(string peer, string peerName, byte jumpPhase, byte moveState, bool isTurning, ushort mountId,
                          ushort resolvedTarget, ushort lastAppliedAnim, bool emoteActive)
    {
        if (!Armed) return;
        var t = TraceFor(peer);
        var who = string.IsNullOrEmpty(peerName) ? peer.Substring(0, Math.Min(6, peer.Length)) : peerName;

        // Jump-phase transition (the headline signal for the double-loop).
        if (jumpPhase != t.LastJumpPhase)
        {
            log.Information("[HMSync] [LOCODIAG] " + who + " jumpPhase " + t.LastJumpPhase + "→" + jumpPhase +
                (t.Init ? " (prev phase held " + t.PhaseFrameCount + " frames)" : ""));
            t.LastJumpPhase = jumpPhase;
            t.PhaseFrameCount = 1;
        }
        else t.PhaseFrameCount++;

        // Move-state transition (walk→idle etc - the walk-stop signal).
        if (moveState != t.LastMoveState)
        {
            log.Information("[HMSync] [LOCODIAG] " + who + " moveState " + t.LastMoveState + "→" + moveState +
                (isTurning ? " (turning)" : ""));
            t.LastMoveState = moveState;
        }

        // Mount transition (mount→0 is the dismount, the sibling symptom).
        if (mountId != t.LastMountId)
        {
            log.Information("[HMSync] [LOCODIAG] " + who + " mountId " + t.LastMountId + "→" + mountId +
                (mountId == 0 ? " (DISMOUNT)" : ""));
            t.LastMountId = mountId;
        }

        // Resolved-target transition + sustain count (the receiver-side over-sustain signal).
        if (resolvedTarget != t.LastTarget)
        {
            log.Information("[HMSync] [LOCODIAG] " + who + " target tl " + t.LastTarget + "→" + resolvedTarget +
                " (prev tl sustained " + t.TargetFrameCount + " frames, " + t.PlayFiresThisTarget + " PlayTimeline fires)" +
                (emoteActive ? " [emote active]" : ""));
            t.LastTarget = resolvedTarget;
            t.TargetFrameCount = 1;
            t.PlayFiresThisTarget = 0;
        }
        else t.TargetFrameCount++;

        t.Init = true;
    }

    /// <summary>Called when the resolver actually fires PlayTimeline (vs just re-asserting BaseOverride).</summary>
    public void OnPlayTimeline(string peer, ushort tl)
    {
        if (!Armed) return;
        var t = TraceFor(peer);
        t.PlayFiresThisTarget++;
        // A 2nd+ fire for the same target is the smoking gun for a re-triggered one-shot.
        if (t.PlayFiresThisTarget >= 2)
            log.Information("[HMSync] [LOCODIAG]   ⚠ PlayTimeline(" + tl + ") RE-FIRED (#" + t.PlayFiresThisTarget +
                ") for same target - one-shot being re-triggered?");
    }
}
