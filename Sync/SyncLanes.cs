using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HMSync.Sync;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
// SYNC-LANE SCAFFOLDING - Stage 1 (S329a)
//
// This file is SCAFFOLDING ONLY. Nothing here is wired into the send/receive path yet - the wire still carries the
// monolithic TransformUpdate (0x10). What this establishes:
//   1. The lane enum (which lane each field belongs to).
//   2. The authoritative field→lane CENSUS MAP (every TransformData render field assigned to exactly one lane).
//   3. A reflection-based validator (LaneCensus.Validate) that asserts the map is COMPLETE and DISJOINT - no field
//      orphaned (in no lane), none double-assigned. This is the anti-orphan guard: the silent failure mode of the
//      refactor is a field that lands in no lane and quietly stops syncing. This test makes that impossible to ship.
//
// Stage 2 splits the SENDER to emit per-lane using this map. Stage 3 splits the RECEIVER onto a per-peer composite.
// The map here is the single source of truth both stages build against.
//
// COUPLED FIELDS STAY CO-LANE (architecture decision): mount id + mount pitch, minion id + its offsets - so a coupled
// change never splits across a frame boundary. That's why MountPitch is HOT-adjacent conceptually but the mount BLOCK
// lives in WARM; the coupling that matters for MountPitch is with position (it's per-frame flight attitude), so it
// rides HOT with position. Minion offsets ride WARM with the minion identity (they only matter while a minion is out).
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Which sync lane a field travels on. Cadences differ per lane - that's the whole point of the split.</summary>
public enum SyncLane
{
    /// <summary>Position/rotation/movement - up to 10Hz while moving, silent when still. The only high-rate lane.</summary>
    Hot,
    /// <summary>Emote/mount/minion/ornament/target/weapon - emitted on change (event-driven).</summary>
    Warm,
    /// <summary>Moniker + cosmetic toggles - session-start + on change.</summary>
    Cold,
    /// <summary>Map-state block (host-authoritative) - emitted on host map-state change only.</summary>
    Host,
}

/// <summary>
/// The authoritative field→lane assignment for every render field of TransformData, plus a validator that proves the
/// assignment is complete and disjoint. Stages 2/3 build the sender split and receiver composite against THIS map.
/// </summary>
public static class LaneCensus
{
    // Fields that are NOT render state - envelope/identity/ordering. Excluded from lane assignment by design.
    public static readonly HashSet<string> NonRenderFields = new()
    {
        "Seq",              // ordering/dedup - envelope concern, not a lane
        "Protocol",         // wire version - envelope concern
        "SenderContentId",  // identity binding - travels on every lane's envelope, not a lane payload field
    };

    // The map: every render field → its lane. This is the single source of truth. Adding a field to TransformData and
    // not adding it here (or to NonRenderFields) fails LaneCensus.Validate - that's the guard.
    public static readonly Dictionary<string, SyncLane> Map = new()
    {
        // ── HOT: position / rotation / movement / per-frame flight attitude / body-draw offset ──
        ["X"] = SyncLane.Hot,
        ["Y"] = SyncLane.Hot,
        ["Z"] = SyncLane.Hot,
        ["Rotation"] = SyncLane.Hot,
        ["MountPitch"] = SyncLane.Hot,          // per-frame flight attitude - coupled with POSITION, rides HOT
        ["MoveState"] = SyncLane.Hot,
        ["MoveMode"] = SyncLane.Hot,
        ["JumpPhase"] = SyncLane.Hot,
        ["IsTurning"] = SyncLane.Hot,
        ["BodyDrawOffsetX"] = SyncLane.Hot,     // swim/sit body offset - per-frame positional, rides HOT
        ["BodyDrawOffsetY"] = SyncLane.Hot,
        ["BodyDrawOffsetZ"] = SyncLane.Hot,
        ["BodyDrawOffsetEpoch"] = SyncLane.Hot, // its gate rides with it

        // ── WARM: emote / pose / mount block / minion block / ornament block / weapon / standup ──
        ["TargetEntityId"] = SyncLane.Warm,     // gaze/target
        ["FaceCamera"] = SyncLane.Warm,         // /facecamera fourth-wall stare
        ["FaceCamX"] = SyncLane.Warm,           // frozen camera-eye point snapshotted at activation
        ["FaceCamY"] = SyncLane.Warm,
        ["FaceCamZ"] = SyncLane.Warm,
        ["GazeEyesOn"] = SyncLane.Warm, ["GazeEyesX"] = SyncLane.Warm, ["GazeEyesY"] = SyncLane.Warm, ["GazeEyesZ"] = SyncLane.Warm,
        ["GazeBodyOn"] = SyncLane.Warm, ["GazeBodyX"] = SyncLane.Warm, ["GazeBodyY"] = SyncLane.Warm, ["GazeBodyZ"] = SyncLane.Warm,
        ["GazeHeadOn"] = SyncLane.Warm, ["GazeHeadX"] = SyncLane.Warm, ["GazeHeadY"] = SyncLane.Warm, ["GazeHeadZ"] = SyncLane.Warm,
        // skills (COSM_1_016) - cosmetic action replay, fire-and-forget on ActionEpoch
        ["ActionId"] = SyncLane.Warm, ["ActionType"] = SyncLane.Warm, ["ActionEpoch"] = SyncLane.Warm,
        ["ActionTgtX"] = SyncLane.Warm, ["ActionTgtY"] = SyncLane.Warm, ["ActionTgtZ"] = SyncLane.Warm,
        ["ActionTgtCid"] = SyncLane.Warm,
        ["EmoteId"] = SyncLane.Warm,
        ["TimelineId"] = SyncLane.Warm,
        ["EmoteEpoch"] = SyncLane.Warm,
        ["PoseType"] = SyncLane.Warm,
        ["CPoseState"] = SyncLane.Warm,
        ["CharMode"] = SyncLane.Warm,
        ["CharModeParam"] = SyncLane.Warm,
        ["MountId"] = SyncLane.Warm,            // mount BLOCK stays together (id + anim + action)
        ["MountAnimTimeline"] = SyncLane.Warm,
        ["MountActionTimeline"] = SyncLane.Warm,
        ["MountActionEpoch"] = SyncLane.Warm,
        ["MinionId"] = SyncLane.Warm,           // minion BLOCK stays together (id + behaviour + anim + offsets)
        ["MinionBehaviour"] = SyncLane.Warm,
        ["MinionAnim"] = SyncLane.Warm,
        ["MinionOffX"] = SyncLane.Warm,         // minion offsets coupled with minion identity, not player position
        ["MinionOffY"] = SyncLane.Warm,
        ["MinionOffZ"] = SyncLane.Warm,
        ["MinionRot"] = SyncLane.Warm,
        ["OrnamentId"] = SyncLane.Warm,         // ornament BLOCK stays together
        ["OrnamentTimeline"] = SyncLane.Warm,
        ["OrnamentActionTimeline"] = SyncLane.Warm,
        ["OrnamentActionEpoch"] = SyncLane.Warm,
        ["WeaponDrawn"] = SyncLane.Warm,
        ["StandupTimelineId"] = SyncLane.Warm,  // standup one-shot (candidate to move to EVENT lane in a later stage)
        ["StandupEpoch"] = SyncLane.Warm,

        // ── COLD: Moniker + cosmetic toggles (session-start + on change) ──
        ["MonikerName"] = SyncLane.Cold,
        ["MonikerHideFc"] = SyncLane.Cold,
        ["MonikerHideName"] = SyncLane.Cold,
        ["VisorToggled"] = SyncLane.Cold,
        ["HatHidden"] = SyncLane.Cold,

        // ── HOST: map-state block (host-authoritative) ──
        ["MapWeatherId"] = SyncLane.Host,
        ["MapTimeForced"] = SyncLane.Host,
        ["MapEorzeaHour"] = SyncLane.Host,
        ["MapEorzeaMinute"] = SyncLane.Host,
        ["MapBgmId"] = SyncLane.Host,
        ["MapRemoveNpcs"] = SyncLane.Host,
        ["MapHideQuestSigns"] = SyncLane.Host,
        ["MapStateEpoch"] = SyncLane.Host,
    };

    /// <summary>
    /// The anti-orphan guard. Reflects over every public get/set property of TransformData and asserts each is EITHER
    /// in NonRenderFields OR in Map - exactly once, never both, never neither. Returns null if the census is valid,
    /// or a description of the problem. Call at plugin init (fail loud) and from any test harness.
    /// </summary>
    public static string? Validate()
    {
        var props = typeof(TransformData)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Select(p => p.Name)
            .ToHashSet();

        var problems = new List<string>();

        // 1. Every TransformData property is accounted for (in Map XOR NonRenderFields).
        foreach (var name in props)
        {
            bool inMap = Map.ContainsKey(name);
            bool inNonRender = NonRenderFields.Contains(name);
            if (inMap && inNonRender)
                problems.Add($"'{name}' is in BOTH the lane Map and NonRenderFields - pick one.");
            else if (!inMap && !inNonRender)
                problems.Add($"'{name}' is ORPHANED - in no lane and not marked non-render. Add it to LaneCensus.Map " +
                             $"(or NonRenderFields if it's envelope/identity). This is the exact failure the census guards against.");
        }

        // 2. Every Map/NonRender entry corresponds to a real property (catch typos + removed fields).
        foreach (var name in Map.Keys)
            if (!props.Contains(name))
                problems.Add($"LaneCensus.Map references '{name}', which is not a TransformData property (typo or removed field).");
        foreach (var name in NonRenderFields)
            if (!props.Contains(name))
                problems.Add($"NonRenderFields references '{name}', which is not a TransformData property (typo or removed field).");

        return problems.Count == 0 ? null : string.Join(Environment.NewLine, problems);
    }

    /// <summary>All render fields assigned to a given lane. Stage 2's sender uses this to build each lane payload.</summary>
    public static IEnumerable<string> FieldsForLane(SyncLane lane) =>
        Map.Where(kv => kv.Value == lane).Select(kv => kv.Key);

    /// <summary>Count of render fields per lane - for logging/diagnostics.</summary>
    public static Dictionary<SyncLane, int> LaneCounts() =>
        Map.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Count());
}
