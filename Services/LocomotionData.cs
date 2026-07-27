using System;
using System.Collections.Generic;

namespace HMSync.Services;

/// <summary>
/// Complete locomotion timeline mapping from FFXIV ActionTimeline sheet.
/// All IDs confirmed via datamined ActionTimeline.csv + Brio TimelineIdentification.
/// </summary>
public static class LocomotionData
{
    // ── Movement modes ──
    public const byte ModeGround = 0;
    public const byte ModeSwimSurface = 1;
    public const byte ModeSwimUnder = 2;
    public const byte ModeFlyMount = 3;
    public const byte ModeGroundMount = 4;
    public const byte ModeSwimMount = 5;

    // ── Speed classes (MoveState) ──
    public const byte SpeedIdle = 0;
    public const byte SpeedWalk = 1;
    public const byte SpeedRun = 2;
    public const byte SpeedSprint = 3;

    // ── Direction ──
    public const byte DirForward = 0;
    public const byte DirLeft = 1;
    public const byte DirRight = 2;
    public const byte DirBackward = 3;

    // Quadrant boundaries (radians) for binning movement direction relative to
    // facing: forward < 45°, left 45°–135°, backward > 135°, right -45°–-135°.
    // S322i: forward/back vs strafe boundaries, widened from 45°/135°. A diagonal walk (forward + strafe, e.g.
    // W+E or W+D+RMB) travels ~45° off the heading and sat right on the old 45° edge, so the receiver's per-frame
    // bin flickered Forward↔strafe and the legs twitched as two clips fought. In standard movement the character
    // plays the FORWARD walk along a diagonal, so binning a diagonal to Forward both kills the flicker and matches
    // the sender; pure strafe (90°) still lands Left/Right. Paired with DirHysteresis for a flip-proof dead-band.
    private const float QuadrantNear = MathF.PI / 3f;       // 60°
    private const float QuadrantFar = 2f * MathF.PI / 3f;   // 120°
    private const float MinMoveDeltaSq = 0.001f;            // ignore sub-threshold jitter
    private const float DirHysteresis = MathF.PI / 18f;     // 10° dead-band on the forward/strafe boundary
    public const float MinTurnDelta = 0.02f;                // min rotation (rad) to count as a keyboard turn

    // ── Jump phases ──
    public const byte JumpNone = 0;
    public const byte JumpStart = 1;
    public const byte JumpFalling = 2;
    public const byte JumpLanding = 3;

    // ── Weapon transition (Slot=1 in ActionTimeline sheet) ──
    public const ushort WeaponDraw = 1;     // "battle/battle_start"
    public const ushort WeaponSheathe = 2;  // "battle/battle_end"

    // ── Ground: Unarmed ──
    public const ushort GndIdle = 3;
    public const ushort GndTurnL = 7;
    public const ushort GndTurnR = 8;
    public const ushort GndWalkF = 13;
    public const ushort GndWalkL = 14;
    public const ushort GndWalkR = 15;
    public const ushort GndWalkB = 16;
    public const ushort GndRunF = 22;
    public const ushort GndRunL = 23;
    public const ushort GndRunR = 24;
    public const ushort GndRunStart = 25;
    public const ushort GndRunStartBR = 26;
    public const ushort GndRunStartBL = 27;
    public const ushort GndSprint = 30;
    public const ushort GndJumpStart = 31;
    public const ushort GndJumpFall = 32;
    public const ushort GndJumpLand = 33;

    // ── Ground: Armed ──
    public const ushort BtlIdle = 34;
    public const ushort BtlTurnL = 35;
    public const ushort BtlTurnR = 36;
    public const ushort BtlWalkF = 41;
    public const ushort BtlWalkL = 42;
    public const ushort BtlWalkR = 43;
    public const ushort BtlWalkB = 44;
    public const ushort BtlRunF = 50;
    public const ushort BtlRunL = 51;
    public const ushort BtlRunR = 52;
    public const ushort BtlSprint = 58;
    public const ushort BtlJumpStart = 59;
    public const ushort BtlJumpFall = 60;
    public const ushort BtlJumpLand = 61;

    // ── Swimming: Surface ──
    public const ushort SwmOnIdle = 4947;
    public const ushort SwmOnTurnL = 4948;
    public const ushort SwmOnTurnR = 4949;
    public const ushort SwmOnWalkF = 4950;
    public const ushort SwmOnWalkL = 4951;
    public const ushort SwmOnWalkR = 4952;
    public const ushort SwmOnWalkB = 4953;
    public const ushort SwmOnRunF = 4954;
    public const ushort SwmOnRunL = 4955;
    public const ushort SwmOnRunR = 4956;
    public const ushort SwmOnSprint = 4958;
    public const ushort SwmOnJumpStart = 4959;
    public const ushort SwmOnJumpFall = 4960;
    public const ushort SwmOnJumpLand = 4961;

    // ── Swimming: Underwater ──
    public const ushort SwmUnIdle = 4968;
    public const ushort SwmUnTurnL = 4969;
    public const ushort SwmUnTurnR = 4970;
    public const ushort SwmUnWalkF = 4971;
    public const ushort SwmUnWalkL = 4972;
    public const ushort SwmUnWalkR = 4973;
    public const ushort SwmUnWalkB = 4974;
    public const ushort SwmUnRunF = 4975;
    public const ushort SwmUnRunL = 4976;
    public const ushort SwmUnRunR = 4977;
    public const ushort SwmUnSprint = 4979;
    public const ushort SwmUnUp = 4980;
    public const ushort SwmUnDown = 4981;

    // ── Flying mount ──
    public const ushort FlyIdle = 4040;
    public const ushort FlyTurnL = 4041;
    public const ushort FlyTurnR = 4042;
    public const ushort FlyRunF = 4043;
    public const ushort FlyRunL = 4044;
    public const ushort FlyRunR = 4045;
    public const ushort FlyWalkB = 4046;
    public const ushort FlyWalkF = 4053;
    public const ushort FlyWalkL = 4054;
    public const ushort FlyWalkR = 4055;
    public const ushort FlyUp = 4052;
    public const ushort FlyDown = 4056;
    public const ushort FlyGlide = 4049;
    public const ushort FlyTakeoff = 4051;
    public const ushort FlyLanding = 4050;

    // ── Ground mount ──
    public const ushort MntIdle = 166;
    public const ushort MntRun = 167;

    // ── Swim mount ──
    public const ushort SwmMntIdle = 5003;
    public const ushort SwmMntRun = 5004;

    /// <summary>
    /// All timeline IDs that are locomotion (not emotes).
    /// Used by LocalStateDetector to filter these from one-shot emote detection.
    /// </summary>
    public static readonly HashSet<ushort> AllLocomotionTimelines = new()
    {
        // Weapon transitions
        1, 2,
        // Ground unarmed: idle, inactive, turns, walk, run, sprint, jump
        3, 4, 5, 6, 7, 8, 13, 14, 15, 16, 22, 23, 24, 25, 26, 27, 30, 31, 32, 33,
        // Ground armed: idle, turns, walk, run, sprint, jump
        34, 35, 36, 41, 42, 43, 44, 50, 51, 52, 58, 59, 60, 61,
        // Ground mount
        165, 166, 167, 168, 190, 191,
        // Flying mount
        4040, 4041, 4042, 4043, 4044, 4045, 4046, 4047, 4048, 4049,
        4050, 4051, 4052, 4053, 4054, 4055, 4056, 4057, 4058,
        // Swim surface
        4947, 4948, 4949, 4950, 4951, 4952, 4953, 4954, 4955, 4956, 4957, 4958,
        4959, 4960, 4961, 4962, 4963,
        // Swim underwater
        4968, 4969, 4970, 4971, 4972, 4973, 4974, 4975, 4976, 4977, 4978, 4979,
        4980, 4981, 4982, 4983, 4984, 4985,
        // Swim mount
        5003, 5004, 5005,
    };

    /// <summary>
    /// Detect movement mode from a timeline ID.
    /// Returns the MoveMode byte for the given timeline.
    /// </summary>
    public static byte DetectModeFromTimeline(ushort tl)
    {
        if (tl >= 4968 && tl <= 4985) return ModeSwimUnder;
        if (tl >= 4947 && tl <= 4963) return ModeSwimSurface;
        if (tl >= 4040 && tl <= 4058) return ModeFlyMount;
        if (tl >= 5003 && tl <= 5005) return ModeSwimMount;
        if (tl == 166 || tl == 167 || tl == 168 || tl == 165 || tl == 190 || tl == 191) return ModeGroundMount;
        return ModeGround;
    }

    /// <summary>
    /// Check if a timeline ID is a jump animation.
    /// </summary>
    public static byte DetectJumpPhase(ushort tl)
    {
        if (tl == GndJumpStart || tl == BtlJumpStart || tl == SwmOnJumpStart) return JumpStart;
        if (tl == GndJumpFall || tl == BtlJumpFall || tl == SwmOnJumpFall) return JumpFalling;
        if (tl == GndJumpLand || tl == BtlJumpLand || tl == SwmOnJumpLand) return JumpLanding;
        return JumpNone;
    }

    /// <summary>
    /// Check if a timeline ID is a sprint animation.
    /// </summary>
    public static bool IsSprint(ushort tl)
    {
        return tl == GndSprint || tl == BtlSprint || tl == SwmOnSprint || tl == SwmUnSprint;
    }

    /// <summary>
    /// Check if a timeline ID is a turn-in-place (keyboard turn) animation.
    /// Mouse pivot rotates silently and does not play these.
    /// </summary>
    public static bool IsTurnTimeline(ushort tl)
    {
        return tl == GndTurnL || tl == GndTurnR
            || tl == BtlTurnL || tl == BtlTurnR
            || tl == SwmOnTurnL || tl == SwmOnTurnR
            || tl == SwmUnTurnL || tl == SwmUnTurnR
            || tl == FlyTurnL || tl == FlyTurnR;
    }

    /// <summary>
    /// Classify movement direction relative to facing, from a position delta.
    /// dx/dz is the world-space movement since the previous frame; facing is the
    /// actor's rotation. Returns one of Dir{Forward,Left,Right,Backward}.
    /// Movement below the jitter threshold is treated as forward.
    /// </summary>
    public static byte ComputeDirection(float dx, float dz, float facing, byte previousDir)
    {
        if (dx * dx + dz * dz <= MinMoveDeltaSq)
            return DirForward;

        var moveAngle = MathF.Atan2(dx, dz);
        var rel = WrapAngle(moveAngle - facing);
        var absRel = MathF.Abs(rel);

        // Hysteresis on the forward/strafe boundary (where the diagonal-walk twitch lives; the backward
        // boundary at 120° is nowhere near a 45° diagonal). If we're already going Forward, demand a touch
        // MORE sideways before switching to a strafe; if we're already strafing, demand a touch more frontal
        // before returning. The dead-band stops a heading sitting on the line from flipping the bin
        // frame-to-frame and restarting the clip — the leg twitch.
        float near = QuadrantNear;
        if (previousDir == DirForward) near += DirHysteresis;
        else if (previousDir == DirLeft || previousDir == DirRight) near -= DirHysteresis;

        if (absRel >= QuadrantFar) return DirBackward;
        if (absRel >= near) return rel > 0 ? DirLeft : DirRight;
        return DirForward;
    }

    /// <summary>Wrap an angle to the range (-π, π].</summary>
    public static float WrapAngle(float angle)
    {
        while (angle > MathF.PI) angle -= MathF.Tau;
        while (angle < -MathF.PI) angle += MathF.Tau;
        return angle;
    }

    /// <summary>
    /// Get the correct locomotion timeline for the given parameters.
    /// </summary>
    public static ushort GetTimeline(byte mode, bool armed, byte speed, byte direction)
    {
        return mode switch
        {
            ModeSwimSurface => GetSwimSurfaceTimeline(speed, direction),
            ModeSwimUnder => GetSwimUnderTimeline(speed, direction),
            ModeFlyMount => GetFlyTimeline(speed, direction),
            // S197b: GroundMount MOVEMENT uses the on-foot Gnd* timelines (run F/L/R, sprint) — the
            // receiver PROVED it animates a mounted puppet with these (reverse-mount: full run/strafe/
            // turn). Only the IDLE stays MntIdle, so the stop case applies the seated mount idle rather
            // than clearing BaseOverride to 0 (which skated/stuck — the S197 regression). This gives
            // animated movement AND a clean stop. (MntRun 167 was the wrong driver — never animated.)
            ModeGroundMount => speed == SpeedIdle
                ? MntIdle
                : (armed ? GetBattleTimeline(speed, direction) : GetNormalTimeline(speed, direction)),
            ModeSwimMount => speed == SpeedIdle ? SwmMntIdle : SwmMntRun,
            _ => armed ? GetBattleTimeline(speed, direction) : GetNormalTimeline(speed, direction),
        };
    }

    public static ushort GetTurnTimeline(byte mode, bool armed, bool left)
    {
        return mode switch
        {
            ModeSwimSurface => left ? SwmOnTurnL : SwmOnTurnR,
            ModeSwimUnder => left ? SwmUnTurnL : SwmUnTurnR,
            ModeFlyMount => left ? FlyTurnL : FlyTurnR,
            _ => armed
                ? (left ? BtlTurnL : BtlTurnR)
                : (left ? GndTurnL : GndTurnR),
        };
    }

    /// <summary>
    /// S328aj: is this timeline a one-shot LANDING terminal clip (jump/dismount fall→land)? Landing clips must play
    /// ONCE and release, not be sustained by BaseOverride — sustaining past the clip's natural length re-shows the
    /// end-of-clip knee-bend squat (the "double-squat" glitch). Everything else (walk/run/idle loops) is sustained.
    /// </summary>
    public static bool IsLandingClip(ushort tl) => tl == GndJumpLand || tl == BtlJumpLand || tl == SwmOnJumpLand;

    public static ushort GetJumpTimeline(byte mode, bool armed, byte jumpPhase)
    {
        if (mode == ModeSwimSurface)
        {
            return jumpPhase switch
            {
                JumpStart => SwmOnJumpStart,
                JumpFalling => SwmOnJumpFall,
                JumpLanding => SwmOnJumpLand,
                _ => (ushort)0,
            };
        }

        if (armed)
        {
            return jumpPhase switch
            {
                JumpStart => BtlJumpStart,
                JumpFalling => BtlJumpFall,
                JumpLanding => BtlJumpLand,
                _ => (ushort)0,
            };
        }

        return jumpPhase switch
        {
            JumpStart => GndJumpStart,
            JumpFalling => GndJumpFall,
            JumpLanding => GndJumpLand,
            _ => (ushort)0,
        };
    }

    private static ushort GetNormalTimeline(byte speed, byte direction)
    {
        return speed switch
        {
            SpeedSprint => GndSprint,
            SpeedRun => direction switch
            {
                DirLeft => GndRunL,
                DirRight => GndRunR,
                _ => GndRunF,
            },
            SpeedWalk => direction switch
            {
                DirLeft => GndWalkL,
                DirRight => GndWalkR,
                DirBackward => GndWalkB,
                _ => GndWalkF,
            },
            _ => GndIdle,
        };
    }

    private static ushort GetBattleTimeline(byte speed, byte direction)
    {
        return speed switch
        {
            SpeedSprint => BtlSprint,
            SpeedRun => direction switch
            {
                DirLeft => BtlRunL,
                DirRight => BtlRunR,
                _ => BtlRunF,
            },
            SpeedWalk => direction switch
            {
                DirLeft => BtlWalkL,
                DirRight => BtlWalkR,
                DirBackward => BtlWalkB,
                _ => BtlWalkF,
            },
            _ => BtlIdle,
        };
    }

    private static ushort GetSwimSurfaceTimeline(byte speed, byte direction)
    {
        return speed switch
        {
            SpeedSprint => SwmOnSprint,
            SpeedRun => direction switch
            {
                DirLeft => SwmOnRunL,
                DirRight => SwmOnRunR,
                _ => SwmOnRunF,
            },
            SpeedWalk => direction switch
            {
                DirLeft => SwmOnWalkL,
                DirRight => SwmOnWalkR,
                DirBackward => SwmOnWalkB,
                _ => SwmOnWalkF,
            },
            _ => SwmOnIdle,
        };
    }

    private static ushort GetSwimUnderTimeline(byte speed, byte direction)
    {
        return speed switch
        {
            SpeedSprint => SwmUnSprint,
            SpeedRun => direction switch
            {
                DirLeft => SwmUnRunL,
                DirRight => SwmUnRunR,
                _ => SwmUnRunF,
            },
            SpeedWalk => direction switch
            {
                DirLeft => SwmUnWalkL,
                DirRight => SwmUnWalkR,
                DirBackward => SwmUnWalkB,
                _ => SwmUnWalkF,
            },
            _ => SwmUnIdle,
        };
    }

    private static ushort GetFlyTimeline(byte speed, byte direction)
    {
        return speed switch
        {
            SpeedRun or SpeedSprint => direction switch
            {
                DirLeft => FlyRunL,
                DirRight => FlyRunR,
                _ => FlyRunF,
            },
            SpeedWalk => direction switch
            {
                DirLeft => FlyWalkL,
                DirRight => FlyWalkR,
                DirBackward => FlyWalkB,
                _ => FlyWalkF,
            },
            _ => FlyIdle,
        };
    }
}
