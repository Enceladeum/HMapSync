using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace HMSync.Services;

/// <summary>
/// Detects the local player's actor state — emote, move mode, jump phase,
/// sprint, turning, and weapon draw. Pulled once per capture tick via
/// <see cref="Detect"/>, which returns an immutable <see cref="LocalActorState"/>.
///
/// This service does not register a framework update, send messages, or apply
/// state to peers. It owns only the change-tracking state needed to compute
/// emote epochs across calls. StateCaptureService drives it on the capture
/// cadence, so there is no cross-service ordering dependency.
/// </summary>
public unsafe class LocalStateDetector
{
    // S304: gate the dev traces (POSEDIAG, cpose sustain, weapon/chair/mode/one-shot) behind research
    // mode. Static so it can be flipped from the /hms debug command without threading a reference
    // through. Default OFF = clean shipping log; errors/important lines still use log.* directly.
    public static bool Verbose;
    private void Dbg(string msg) { if (Verbose) log.Debug(msg); }

    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    // S328w: gates the verbose LOCOTRACE logging (the per-movement truth-table trace). Off by default; set from the
    // debug-mode config so LOCOTRACE only fires when debug mode is on. Reduces log spam in normal use.
    public bool DebugTrace;
    private ushort lastOrnamentLogId; // S322l: throttle the ornament-id debug log to changes
    private ushort ornActionTimeline; // S323g: tl0 of the current ornament action one-shot
    private uint ornActionEpoch;      // S323g: bumps per new ornament action
    private ushort lastOrnActionTl;   // S323g: edge-detect a new action vs the same one held
    private ushort mountActionTimeline; // S323j: tl0 of the current mount action one-shot (on the mount object)
    private uint mountActionEpoch;      // S323j: bumps per new mount action
    private ushort lastMountActionTl;   // S323j: edge-detect a new mount action vs the same one held
    private byte prevCPoseState;        // S323k: last tick's CPoseState — distinguishes a hold-intro (cpose changed) from an action (cpose stable)

    private bool active;

    // Change-tracking state (persists across Detect calls)
    private CharacterModes lastMode;
    private byte lastModeParam;
    private ushort lastTimelineId;
    private long emoteEndCooldownUntil;
    private bool lastWeaponDrawn;
    private byte lastCPoseState;
    private byte lastPoseType;
    private bool poseInit;

    // Emote state carried between calls (the "current" emote being reported)
    private ushort currentEmoteId;
    private ushort currentTimelineId;
    private ushort lastPoseIntro; // S107: intro of the currently-held cpose (reassume detection)
    private readonly ushort[] lastUpperSlots = new ushort[4]; // S122: seated overlay slot watch
    private uint emoteEpoch;

    // S55: standup detection — fires when the game's get-up timeline appears
    // on ActionTimeline[0] while still in a seated mode, i.e. at the START of
    // the standup process (before mode transitions to Normal).
    private ushort standupTimelineId;
    private uint standupEpoch;
    private bool standupFired; // prevents re-firing while TL stays active

    // Lookup tables (built once from the Emote sheet)
    private readonly Dictionary<ushort, ushort> timelineToEmoteId = new();
    private readonly HashSet<ushort> emoteTimelineIds = new();
    private readonly Dictionary<byte, ushort> emoteModeToEmoteId = new();

    // S55: modeParam → get-up timeline (e.g. 2→644, 1→655). Built from EmoteMode sheet.
    private readonly Dictionary<byte, ushort> modeParamToStandupTimeline = new();
    private readonly HashSet<ushort> standupTimelines = new();

    // ── CHAIRTRACE: per-tick animation state logging while seated ──
    // Runs automatically in InPositionLoop/EmoteLoop + 2s tail after exit.
    // Only logs on state changes to avoid spam. Grep for [CHAIRTRACE].
    private bool chairTraceActive;
    private long chairTraceExitTime; // when we left seated mode, for the 2s tail

    // WEAPONTRACE fields
    private bool weaponTraceActive;
    private long weaponTraceExitTime;
    private string lastWeaponTraceKey = "";
    private string lastOrnTraceSig = "";   // S323d: ORNAMENTTRACE change-detection signature
    private string lastMountTraceSig = ""; // S323i: MOUNTACTIONTRACE change-detection signature
    private ushort ctLastTL0, ctLastTL1, ctLastTL2, ctLastTL3;
    private ushort ctLastBO;
    private float ctLastPosY, ctLastDrawY;
    private byte ctLastMode, ctLastParam;

    public LocalStateDetector(
        IObjectTable objectTable,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.log = log;

        BuildLookupTables();
    }

    /// <summary>Resets change-tracking state at the start of a session.</summary>
    // v0.7.413: baseline from the LIVE actor, not a hardcoded Normal.
    //
    // Reset used to assert lastMode = Normal regardless of what the character was actually doing. If a
    // session engages while the player is SEATED, the detector therefore believes they were already
    // standing — and its standup emission requires
    //     timelineChanged && (isSeated || (modeChanged && wasSeated)) && standupTimelines.Contains(tl)
    // so `wasSeated` is false, `modeChanged` is false, and a subsequent stand-up is UNOBSERVABLE. The
    // peer never receives StandupEpoch, and their seated branch never writes CharMode by design, so the
    // puppet stays in InPositionLoop for the whole session.
    //
    // Baselining from the live actor makes the seated→standing transition a real, detectable edge.
    public void Reset()
    {
        active = true;

        var lp = objectTable.LocalPlayer;
        var ch = lp != null ? (Character*)lp.Address : null;
        if (ch != null)
        {
            lastMode = ch->Mode;
            lastModeParam = ch->ModeParam;
            lastTimelineId = (ushort)ch->Timeline.TimelineSequencer.TimelineIds[0];
        }
        else
        {
            lastMode = CharacterModes.Normal;
            lastModeParam = 0;
            lastTimelineId = 0;
        }
        emoteEndCooldownUntil = 0;
        lastWeaponDrawn = false;
        currentEmoteId = 0;
        currentTimelineId = 0;
        lastPoseIntro = 0;
        emoteEpoch = 0;
        standupTimelineId = 0;
        standupEpoch = 0;
        standupFired = false;
        lastCPoseState = 0;
        lastPoseType = 0;
        poseInit = false;
        log.Information("[HMSync] Local state detector reset");
    }

    public void Stop()
    {
        active = false;
    }


    /// <summary>
    /// Reads the local player's current actor state. Returns null if there is
    /// no local player or detection is inactive. Updates internal emote-epoch
    /// tracking as a side effect.
    /// </summary>
    public LocalActorState? Detect()
    {
        if (!active) return null;

        var player = objectTable.LocalPlayer;
        if (player == null) return null;

        var character = (Character*)player.Address;
        var currentMode = character->Mode;
        var currentParam = character->ModeParam;
        var currentTimeline = (ushort)character->Timeline.TimelineSequencer.TimelineIds[0];
        var weaponDrawn = character->Timeline.IsWeaponDrawn;
        var poseType = (byte)character->EmoteController.CurrentPoseType;
        var cPoseState = character->EmoteController.CPoseState;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ── Movement state from current timeline ──
        var moveMode = LocomotionData.DetectModeFromTimeline(currentTimeline);
        var jumpPhase = LocomotionData.DetectJumpPhase(currentTimeline);
        var isSprinting = LocomotionData.IsSprint(currentTimeline);
        var isTurning = LocomotionData.IsTurnTimeline(currentTimeline);
        float mountPitch = 0f; // S206: mount nose pitch (flying only); 0 = level / not flying

        // S197e ITERATION 1 (async): the GroundMount upgrade is REMOVED. The sender now broadcasts
        // PLAIN ON-FOOT locomotion (MoveMode forced to Ground) even when the local player is mounted,
        // so that when self-mount lands (iteration 3) A's broadcast stays on-foot-flavored and every B
        // drives A's puppet through the PROVEN testmount path (mount applied via MountId + on-foot
        // timelines → native mounted animation, all restrictions respected, skate-free, with the free
        // dismount/dismiss animation). This is the inverse of the old bleed: we deliberately do NOT
        // let the local mount change the locomotion flavor on the wire.
        // IMPORTANT: this force is required even though the explicit "if Mounted → GroundMount" upgrade
        // was removed, because DetectModeFromTimeline ALSO returns ModeGroundMount for the seated mount
        // poses (166/167/168/…) the rider holds while mounted — so without this force the GroundMount
        // flavor would survive through that path. Forcing Ground here closes BOTH routes.
        if (currentMode == CharacterModes.Mounted)
            moveMode = LocomotionData.ModeGround;

        // S148: capture the actual mount ID so each receiver can spawn the right mount on A's puppet
        // (the testmount path). 0 when not mounted → receiver dismounts (native dismiss plays).
        // MountContainer.MountId @ Character+0x670+0x18. Reads our mount state whether we mounted via
        // the game menu, the mod UI, or (iteration 3) the self-mount command.
        ushort mountId = currentMode == CharacterModes.Mounted ? character->Mount.MountId : (ushort)0;

        // S322: capture the summoned minion (companion) id so each receiver replicates it on A's puppet.
        // 0 when none → receiver dismisses. The LIVE summoned minion is the spawned companion OBJECT, whose
        // GameObject.BaseId is the Companion sheet row (HaselDebug reads exactly this). CompanionData.CompanionId
        // (0x18) is NOT the live id — it reads 0 here, which is why the summon never synced and a pre-summoned
        // minion never carried. Object null ⇒ no minion out. Mirror of the mount capture above.
        ushort minionId = character->CompanionData.CompanionObject != null
            ? (ushort)character->CompanionData.CompanionObject->BaseId
            : (ushort)0;
        // S322f: capture the minion's runtime Behaviour (CompanionMove enum: None/Obedient/Independent/
        // Stationary) from the SENDER's companion, where it's correct. A receiver's puppet-spawned companion
        // never gets this field set, so it can't tell a stationary minion (campfire/cushion) from a follower —
        // it relies on this sent value to decide whether to drive the follow. 0 when no minion is out.
        byte minionBehaviour = character->CompanionData.CompanionObject != null
            ? (byte)character->CompanionData.CompanionObject->Behavior
            : (byte)0;
        // S322g: capture the minion's live base animation timeline (idle/walk/VFX) so a receiver can replay it
        // on the puppet's companion — same model ⇒ same clip. The puppet's companion AI doesn't animate it
        // natively (it only appears + is position-driven), so this is what makes its legs move. 0 = none.
        ushort minionAnim = character->CompanionData.CompanionObject != null
            ? (ushort)character->CompanionData.CompanionObject->Timeline.TimelineSequencer.TimelineIds[0]
            : (ushort)0;
        // S322h: capture the minion's position OFFSET from its owner + its facing, so a receiver can place a
        // perfect copy at (puppetPos + offset) instead of computing its own follow. Position and the replayed
        // animation then both originate here and stay locked together — no catch-up slide. All 0 when no minion.
        float minionOffX = 0f, minionOffY = 0f, minionOffZ = 0f, minionRot = 0f;
        if (character->CompanionData.CompanionObject != null)
        {
            var ownerPos = character->GameObject.Position;
            // Companion's GameObject base isn't surfaced as a member (only Character's own fields are flattened by
            // [Inherits<Character>]), so reach it by casting to the GameObject base at offset 0 — the same pattern
            // StateApplyService uses to drive the puppet's companion.
            var minionGo = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)character->CompanionData.CompanionObject;
            minionOffX = minionGo->Position.X - ownerPos.X;
            minionOffY = minionGo->Position.Y - ownerPos.Y;
            minionOffZ = minionGo->Position.Z - ownerPos.Z;
            minionRot = minionGo->Rotation;
        }
        // S322k/l: the live ornament id is the container's OrnamentId (@0x18) — confirmed empirically (S323b):
        // while equipped, the object's own OrnamentId (@0x2380) and BaseId (@0x84) both read 0, the OPPOSITE of
        // minions (where the container id reads 0 and BaseId carries it). 0 = none. Ornaments are skeletally
        // attached, so no position/animation sync — SetupOrnament on the puppet is all the apply needs.
        ushort ornamentId = character->OrnamentData.OrnamentId;
        if (ornamentId != lastOrnamentLogId)
        {
            log.Debug("[HMSync] Ornament id " + ornamentId + " on self.");
            lastOrnamentLogId = ornamentId;
        }

        // S323g/h/k: ornament ANIMATION one-shot detection — fully generic, no per-ornament table. Accessory
        // emotes (torch 8194, parasol 8073/8074) and the shovel dig (13383) are tl0 one-shots; the peer copies
        // whatever's broadcast. Detect: an ornament is equipped and tl0 is neither idle (3 = base, 7367 = universal
        // ornament-held idle) nor a locomotion timeline (walking would otherwise look like an action —
        // DetectModeFromTimeline calls both "ground", so the locomotion set is the discriminator).
        //
        // S323k FIX: also require CPoseState to be STABLE this tick. The hold-transition intros (8062/8065/8067)
        // fire ON a cpose change; S323h replayed them so holds would animate, but on the peer the intro animates
        // toward the new stance while the byte-mirror still asserts the OLD CPoseState (which trails a few frames
        // on the wire) — the puppet begins the cpose then snaps back ("yanked to the original stance"). The dig and
        // the emotes both fire at stable cpose, so gating on stability drops the intros (holds go back to a clean
        // byte-mirror snap, as in the confirmed-good S323g) while keeping the dig + emotes. When cpose IS changing,
        // still record the intro's tl0 so it can't fire as an "action" the moment cpose settles a frame later.
        bool cposeStable = cPoseState == prevCPoseState;
        bool isOrnAction = ornamentId != 0
            && currentTimeline != 3 && currentTimeline != 7367
            && !LocomotionData.AllLocomotionTimelines.Contains(currentTimeline);
        if (isOrnAction && cposeStable)
        {
            if (currentTimeline != lastOrnActionTl)
            {
                ornActionTimeline = currentTimeline;
                ornActionEpoch++;
            }
            lastOrnActionTl = currentTimeline;
        }
        else if (isOrnAction)
        {
            lastOrnActionTl = currentTimeline; // cpose changing → hold intro; record so it won't fire as an action when cpose settles
        }
        else
        {
            lastOrnActionTl = 0; // idle/hold/locomotion — let the same action re-fire next time
        }
        prevCPoseState = cPoseState;

        // S323j: mount ACTION one-shot capture — the mount analog of the ornament channel above, confirmed by
        // MOUNTACTIONTRACE: mount-hotbar actions (mount-17's ground-target 1752 + machine-gun 1753, Fenrir howl,
        // mount music, etc.) are pure tl0 one-shots on the MOUNT OBJECT's slot 0, while the rider stays seated
        // (166). AllLocomotionTimelines already contains every value the mount shows in motion — idle (3),
        // turns (7/8), jumps (31/32/33), walk/run, the mounted poses (165–168), fly (4040–4058), swim — so a
        // slot-0 value OUTSIDE that set is an action. Latch it + bump an epoch on the rising edge; the peer
        // replays it once on its OWN mount object. No per-mount table — the peer copies whatever's broadcast.
        ushort mountActSlot0 = 0;
        if (currentMode == CharacterModes.Mounted)
        {
            var maObj = character->Mount.MountObject;
            if (maObj != null)
                mountActSlot0 = (ushort)maObj->Timeline.TimelineSequencer.TimelineIds[0];
        }
        // S323j: a slot-0 value OUTSIDE the locomotion set is a mount ACTION one-shot (hotbar action, howl,
        // etc.), latched + epoch-bumped for the peer to replay once on its own mount object.
        // v0.7.450: takeoff (4051) and landing (4050) are TRANSITION one-shots that live INSIDE the fly
        // locomotion range (4040–4058), so the plain "outside AllLocomotionTimelines" test excluded them —
        // and the receiver's steady-flight resolver (GetFlyTimeline: idle/run/turn by speed+direction) has
        // no path to emit them, so peers saw the rider snap ground↔flight with no spring-up/descend blend.
        // Route them through this SAME one-shot channel (the proven mount-action replay path) by treating
        // them as actions here. They're brief, non-looping (IsLoop=False in the sheet), and self-clear, so
        // replaying once on the peer's mount object plays the transition blend exactly like a hotbar action.
        bool isFlightTransition = mountActSlot0 == LocomotionData.FlyTakeoff
                               || mountActSlot0 == LocomotionData.FlyLanding;
        if (mountActSlot0 != 0 && (isFlightTransition || !LocomotionData.AllLocomotionTimelines.Contains(mountActSlot0)))
        {
            if (mountActSlot0 != lastMountActionTl)
            {
                mountActionTimeline = mountActSlot0;
                mountActionEpoch++;
                lastMountActionTl = mountActSlot0;
            }
        }
        else
        {
            lastMountActionTl = 0; // idle/locomotion/dismounted — let the same action re-fire next time
        }

        // ── LOCOTRACE (Phase 0, locomotion refactor): build the movement→timeline TRUTH TABLE. Sender-side, on
        // change. For each movement case — forward/back/strafe-L/strafe-R/turn/sprint — across base / ornament /
        // mount, it records what actually drives the puppet: tl0, BaseOverride, mode, sprint, turn, the computed
        // movement DIRECTION (relative to facing), the raw position delta + facing (to catch the moonwalk facing
        // inversion), armed state, and the mount object's tl0. The resolver's direction model + sender-authoritative
        // timeline sources get designed from THIS, not guesses. dir legend: 0=Fwd 1=Left 2=Right 3=Back. REMOVE with
        // the other traces at refactor Phase 4. S328w: gated behind DebugTrace (debug mode) to avoid log spam.
        // (Phase 0 LOCOTRACE diagnostic block removed — the locomotion resolver's direction/timeline model is
        // designed; the per-frame trace emit is retired for prod.)

        // S197: the rider's TimelineIds[0] holds the seated pose (166/167) while mounted, NOT the jump
        // (31/32/33) or TURN (7/8), which live on the MOUNT OBJECT's slot 0. KEPT/EXTENDED for the
        // async model: derive both jumpPhase AND isTurning from the mount object's slot 0 so a
        // self-mounted jump and A/D turn-in-place still broadcast as plain on-foot jump/turn the
        // receiver animates via the normal path. (Not a skating bleed — sets only jumpPhase/isTurning,
        // never the locomotion mode/speed.)
        if (currentMode == CharacterModes.Mounted)
        {
            var mountObj = character->Mount.MountObject;
            if (mountObj != null)
            {
                ushort mountSlot0 = (ushort)mountObj->Timeline.TimelineSequencer.TimelineIds[0];
                jumpPhase = LocomotionData.DetectJumpPhase(mountSlot0);
                // S197g: A/D rotation-in-place — the turn plays GndTurnL/R (7/8) on the mount object,
                // not the rider, so the rider-timeline check above missed it (turn never reached peers).
                if (LocomotionData.IsTurnTimeline(mountSlot0))
                    isTurning = true;

                // S202 FLIGHT: the force-Ground above (for ground-mount skate-prevention) was ALSO
                // overwriting flight. [FLYDIAG] confirmed: airborne, the mount object's slot 0 shows
                // Fly* (4040–4058 → DetectModeFromTimeline returns ModeFlyMount=3), but broadcastMode
                // was forced to 0 → peers got ground-walk timelines at flight altitude ("walking on
                // air"). Fix: when the mount object's slot 0 is a flight timeline, broadcast
                // ModeFlyMount so the receiver's GetTimeline ModeFlyMount branch (Fly* timelines, already
                // defined) animates flight on the mounted puppet — same as Gnd* animates ground. The
                // puppet's altitude already rides the normal Y position sync, so only the mode was wrong.
                // (Ground-mount still force-Ground from the rule above — skate-free — because its slot 0
                // is Gnd*, not Fly*, so this branch doesn't fire.)
                if (LocomotionData.DetectModeFromTimeline(mountSlot0) == LocomotionData.ModeFlyMount)
                    moveMode = LocomotionData.ModeFlyMount;

                // S206 PITCH: the mount visibly tilts its nose on climb/dive, but we only sync yaw
                // (GameObject.Rotation), so peers saw the mount stay perfectly level while changing
                // altitude. Pitch lives in the mount object's DrawObject rotation quaternion
                // (GameObject+0x100 → Object+0x60). [PITCHDIAG] confirmed it's smeared across the quat's
                // X/Z by yaw (it's a full 3D orientation), so we extract the nose angle directly:
                //   pitch = asin(2*(w*x - y*z))   — validated vs the log: ~0 level, ~-0.7rad climbing,
                //   ~+0.7rad diving. Sent as a scalar (not the raw quat) so it composes on top of the
                //   yaw the receiver already applies, instead of double-applying yaw.
                if (moveMode == LocomotionData.ModeFlyMount)
                {
                    var drawObj = mountObj->DrawObject; // Character inherits GameObject; DrawObject @0x100
                    if (drawObj != null)
                    {
                        var q = drawObj->Rotation; // DrawObject inherits Object; Rotation (Quaternion) @0x60
                        float s = 2f * (q.W * q.X - q.Y * q.Z);
                        s = s > 1f ? 1f : (s < -1f ? -1f : s);
                        mountPitch = MathF.Asin(s);
                    }
                }
            }
        }

        // ── Emote detection (updates currentEmoteId / epoch) ──
        // S122: seated overlay emotes (add_* family: /pray 4814, /yes nod, etc.) play
        // on TL SLOTS 1–3, never touching TL0 — invisible to the TL0-only watch.
        // Stream any nonzero slot-change while seated through the sub-emote channel
        // (epoch + timeline); the receiver's PlayTimeline routes by the sheet's Slot.
        if (currentMode != CharacterModes.Normal)
        {
            for (int slot = 1; slot <= 3; slot++)
            {
                var slotTl = (ushort)character->Timeline.TimelineSequencer.TimelineIds[slot];
                if (slotTl != lastUpperSlots[slot] )
                {
                    lastUpperSlots[slot] = slotTl;
                    if (slotTl > 0)
                    {
                        currentTimelineId = slotTl;
                        emoteEpoch++;
                        Dbg("[HMSync] Seated overlay (slot " + slot + "): tl=" +
                            slotTl + " epoch=" + emoteEpoch);
                    }
                }
            }
        }

        DetectEmoteState(currentMode, currentParam, currentTimeline, poseType, cPoseState, weaponDrawn, now);

        // ── S323w REVERTED (thread IX) ── The cpose-stance→emote-channel route is removed. Emote.csv proves emotes
        // 243/244/253 (stances 1/2/3) have their OWN ActionTimeline set to 8062/8065/8067 — the exact clips that
        // DETACH the accessory on the puppet. So routing the stance through the native emote path was always going to
        // play the detaching clip; it was a longer road to the same 8062. The accessory drop is NOT a timeline/channel
        // problem — it's the ornament's SKELETAL ATTACHMENT (held items are hand-bone-parented; the custom cpose
        // repositions the hand and the puppet never re-establishes the parent). The fix lives receiver-side as a
        // SetupOrnament re-assert on CPoseState change (see StateApplyService ornament reconcile). Nothing to capture
        // here — CPoseState already rides the wire via the byte-mirror.

        // ── Weapon draw logging ──
        if (weaponDrawn != lastWeaponDrawn)
        {
            Dbg("[HMSync] Weapon: " + (weaponDrawn ? "drawn" : "sheathed"));
            lastWeaponDrawn = weaponDrawn;
            weaponTraceActive = true;
            weaponTraceExitTime = 0;
        }

        // ── WEAPONTRACE: log all weapon-related state during draw/cpose/sheathe ──
        // Fires for 5s after any weapon state change, or whenever PoseType is WeaponDrawn.
        // Captures: Stance, EmoteId, PoseType, CPoseState, IsWeaponDrawn, BaseOverride, TL[0]
        {
            bool isWeaponRelated = poseType == 1 // PoseType.WeaponDrawn
                || weaponDrawn;

            if (isWeaponRelated)
            {
                weaponTraceActive = true;
                weaponTraceExitTime = 0;
            }
            else if (weaponTraceActive && weaponTraceExitTime == 0)
            {
                weaponTraceExitTime = now;
            }

            if (weaponTraceActive)
            {
                if (weaponTraceExitTime > 0 && now - weaponTraceExitTime > 5000)
                {
                    weaponTraceActive = false;
                }
                else
                {
                    var stance = character->EmoteController.Stance;
                    var emoteId = character->EmoteController.EmoteId;
                    var baseOverride = character->Timeline.BaseOverride;
                    var tl0 = currentTimeline;

                    // S87: the spawn-packet state fields — how the game tells other
                    // clients how to render this actor's resting pose.
                    // ModelState @ TimelineContainer 0x2C0, AnimationState @ 0x2C1
                    // (2 bytes, documented as "4 bits each").
                    byte* tlBase = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(
                        ref character->Timeline);
                    byte modelState = *(tlBase + 0x2C0);
                    byte animState0 = *(tlBase + 0x2C1);
                    byte animState1 = *(tlBase + 0x2C2);

                    // Break AnimationState into its two nibbles
                    int as0lo = animState0 & 0x0F;
                    int as0hi = (animState0 >> 4) & 0x0F;

                    // Build a composite key for change detection
                    var traceKey = $"{stance}|{emoteId}|{poseType}|{cPoseState}|{weaponDrawn}|{baseOverride}|{tl0}|{currentMode}|{currentParam}|{modelState}|{animState0}|{animState1}";
                    if (traceKey != lastWeaponTraceKey)
                    {
                        Dbg("[WEAPONTRACE] EmoteId=" + emoteId +
                            " PoseType=" + poseType +
                            " CPose=" + cPoseState +
                            " WpnDrawn=" + weaponDrawn +
                            " BO=" + baseOverride +
                            " TL0=" + tl0 +
                            " Mode=" + currentMode + "/" + currentParam +
                            " ModelState=" + modelState +
                            " AnimState=" + animState0 + "(" + as0hi + "/" + as0lo + ")," + animState1 +
                            " Δ");
                        lastWeaponTraceKey = traceKey;
                    }
                }
            }
        }

        // ── ORNAMENTTRACE (S323d): while an ornament is equipped, dump the animation/pose drivers on change so
        // we can see what the shovel-menu actions actually move — dig & put-away are one-shot animations (expect a
        // timeline slot to flick), the cpose hold-change is a resting-pose shift (expect ModelState/AnimationState,
        // the spawn-packet resting-pose fields, and/or CPoseState to move). They DON'T ride the emote channel
        // (EmoteId won't shift), which is why nothing currently propagates. INF + gated on equipped + change, so
        // it's visible without research mode and silent otherwise. Whatever moves here is what we wire to the wire. ──
        if (ornamentId != 0)
        {
            ushort tlo0 = (ushort)character->Timeline.TimelineSequencer.TimelineIds[0];
            ushort tlo1 = (ushort)character->Timeline.TimelineSequencer.TimelineIds[1];
            ushort tlo2 = (ushort)character->Timeline.TimelineSequencer.TimelineIds[2];
            ushort tlo3 = (ushort)character->Timeline.TimelineSequencer.TimelineIds[3];
            ushort ornBaseOv = (ushort)character->Timeline.BaseOverride;
            ushort ornEmoteId = character->EmoteController.EmoteId;
            var ornStance = character->EmoteController.Stance;
            byte* ornTlBase = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref character->Timeline);
            byte ornModelState = *(ornTlBase + 0x2C0);
            byte ornAnimState0 = *(ornTlBase + 0x2C1);
            byte ornAnimState1 = *(ornTlBase + 0x2C2);
            byte ornAttach = 0;
            var ornObjTrace = character->OrnamentData.OrnamentObject;
            if (ornObjTrace != null) ornAttach = (byte)ornObjTrace->CustomizeGroupId;

            var ornSig = tlo0 + "|" + tlo1 + "|" + tlo2 + "|" + tlo3 + "|" + ornBaseOv + "|" + cPoseState + "|" +
                poseType + "|" + ornEmoteId + "|" + ornStance + "|" + ornModelState + "|" + ornAnimState0 + "|" +
                ornAnimState1 + "|" + ornAttach;
            if (ornSig != lastOrnTraceSig)
            {
                log.Information("[HMSync][ORNAMENTTRACE] orn=" + ornamentId +
                    " tl0=" + tlo0 + " tl1=" + tlo1 + " tl2=" + tlo2 + " tl3=" + tlo3 +
                    " BO=" + ornBaseOv + " cpose=" + cPoseState + " pose=" + poseType +
                    " emote=" + ornEmoteId + " stance=" + ornStance +
                    " modelState=" + ornModelState + " animState=" + ornAnimState0 + "," + ornAnimState1 +
                    " attach=" + ornAttach);
                lastOrnTraceSig = ornSig;
            }
        }

        // ── MOUNTACTIONTRACE (S323i): mounts carry the same action hotbar as ornaments (Fenrir howl, mount
        // music, mount-VFX/attack e.g. mount-17 reaper). Ornament actions turned out to be pure tl0 one-shots on
        // the RIDER — but mount actions almost certainly live on the MOUNT OBJECT instead: while mounted the rider
        // holds a seated pose (166/167) and the mount's own jump (31/32/33) / turn (7/8) already sit on the mount
        // object's slot 0 (see the S197 block above). This dumps BOTH timelines on change so we can see which one
        // flicks — and in which slot — when a mount action fires, before cloning the ornament-action channel onto
        // the right target + slot. INF + gated on Mounted + change; REMOVE once mount actions are wired. ──
        if (currentMode == CharacterModes.Mounted)
        {
            var mtObj = character->Mount.MountObject;
            bool mtPresent = mtObj != null;
            ushort mtl0 = 0, mtl1 = 0, mtl2 = 0, mtl3 = 0, mtBO = 0, mtEmote = 0;
            if (mtPresent)
            {
                mtl0 = (ushort)mtObj->Timeline.TimelineSequencer.TimelineIds[0];
                mtl1 = (ushort)mtObj->Timeline.TimelineSequencer.TimelineIds[1];
                mtl2 = (ushort)mtObj->Timeline.TimelineSequencer.TimelineIds[2];
                mtl3 = (ushort)mtObj->Timeline.TimelineSequencer.TimelineIds[3];
                mtBO = (ushort)mtObj->Timeline.BaseOverride;
                mtEmote = mtObj->EmoteController.EmoteId;
            }
            var mtSig = mountId + "|" + currentTimeline + "|" + mtPresent + "|" + mtl0 + "|" + mtl1 + "|" +
                mtl2 + "|" + mtl3 + "|" + mtBO + "|" + mtEmote;
            if (mtSig != lastMountTraceSig)
            {
                log.Information("[HMSync][MOUNTACTIONTRACE] mount=" + mountId +
                    " riderTl0=" + currentTimeline + " mountObj=" + mtPresent +
                    " mTl0=" + mtl0 + " mTl1=" + mtl1 + " mTl2=" + mtl2 + " mTl3=" + mtl3 +
                    " mBO=" + mtBO + " mEmote=" + mtEmote);
                lastMountTraceSig = mtSig;
            }
        }

        // ── CHAIRTRACE: log all animation state while seated + 2s tail ──
        // Captures the exact tick where each field transitions during standup.
        // Only fires on state change. Grep for [CHAIRTRACE] in the log.
        {
            bool isSeatedNow = currentMode == CharacterModes.InPositionLoop
                || currentMode == CharacterModes.EmoteLoop;

            if (isSeatedNow)
            {
                chairTraceActive = true;
                chairTraceExitTime = 0;
            }
            else if (chairTraceActive && chairTraceExitTime == 0)
            {
                chairTraceExitTime = now; // start the 2s tail
            }
            else if (chairTraceActive && now - chairTraceExitTime > 2000)
            {
                chairTraceActive = false; // tail expired
            }

            if (chairTraceActive)
            {
                // Read all observable animation state
                var tl0 = (ushort)character->Timeline.TimelineSequencer.TimelineIds[0];
                var tl1 = (ushort)character->Timeline.TimelineSequencer.TimelineIds[1];
                var tl2 = (ushort)character->Timeline.TimelineSequencer.TimelineIds[2];
                var tl3 = (ushort)character->Timeline.TimelineSequencer.TimelineIds[3];
                var bo = character->Timeline.BaseOverride;
                var posY = character->GameObject.Position.Y;
                var drawY = character->DrawOffset.Y;

                // Log on any change
                if (tl0 != ctLastTL0 || tl1 != ctLastTL1 || tl2 != ctLastTL2
                    || tl3 != ctLastTL3 || bo != ctLastBO
                    || MathF.Abs(posY - ctLastPosY) > 0.001f
                    || MathF.Abs(drawY - ctLastDrawY) > 0.001f
                    || (byte)currentMode != ctLastMode || currentParam != ctLastParam)
                {
                    Dbg("[HMSync][CHAIRTRACE] " +
                        "TL0=" + tl0 + " TL1=" + tl1 + " TL2=" + tl2 + " TL3=" + tl3 +
                        " BO=" + bo +
                        " PosY=" + posY.ToString("F4") +
                        " DrawY=" + drawY.ToString("F4") +
                        " Mode=" + (byte)currentMode + "/" + currentParam +
                        (tl0 != ctLastTL0 ? " ΔTL0" : "") +
                        (tl1 != ctLastTL1 ? " ΔTL1" : "") +
                        (tl2 != ctLastTL2 ? " ΔTL2" : "") +
                        (tl3 != ctLastTL3 ? " ΔTL3" : "") +
                        (bo != ctLastBO ? " ΔBO" : "") +
                        (MathF.Abs(posY - ctLastPosY) > 0.001f ? " ΔPosY" : "") +
                        (MathF.Abs(drawY - ctLastDrawY) > 0.001f ? " ΔDrawY" : "") +
                        ((byte)currentMode != ctLastMode ? " ΔMode" : ""));

                    ctLastTL0 = tl0; ctLastTL1 = tl1; ctLastTL2 = tl2; ctLastTL3 = tl3;
                    ctLastBO = bo; ctLastPosY = posY; ctLastDrawY = drawY;
                    ctLastMode = (byte)currentMode; ctLastParam = currentParam;
                }
            }
        }

        return new LocalActorState(
            emoteId: currentEmoteId,
            timelineId: currentTimelineId,
            emoteEpoch: emoteEpoch,
            charMode: (byte)currentMode,
            charModeParam: currentParam,
            poseType: poseType,
            cPoseState: cPoseState,
            moveMode: moveMode,
            jumpPhase: jumpPhase,
            isSprinting: isSprinting,
            isTurning: isTurning,
            weaponDrawn: weaponDrawn,
            standupTimelineId: standupTimelineId,
            standupEpoch: standupEpoch,
            mountId: mountId,
            minionId: minionId,
            minionBehaviour: minionBehaviour,
            minionAnim: minionAnim,
            minionOffX: minionOffX,
            minionOffY: minionOffY,
            minionOffZ: minionOffZ,
            minionRot: minionRot,
            ornamentId: ornamentId,
            ornamentActionTimeline: ornActionTimeline,
            ornamentActionEpoch: ornActionEpoch,
            mountActionTimeline: mountActionTimeline,
            mountActionEpoch: mountActionEpoch,
            ornamentTimeline: (ornamentId != 0 ? currentTimeline : (ushort)0),
            mountPitch: mountPitch);
    }

    private void DetectEmoteState(CharacterModes currentMode, byte currentParam, ushort currentTimeline, byte poseType, byte cPoseState, bool weaponDrawn, long now)
    {
        bool modeChanged = currentMode != lastMode || currentParam != lastModeParam;
        bool timelineChanged = currentTimeline != lastTimelineId
            && !LocomotionData.AllLocomotionTimelines.Contains(currentTimeline)
            && currentTimeline > 0;

        // ── CPose change is authoritative (standing OR seated) ──
        // CPoseState/PoseType (EmoteController 0x20/0x21) is the game's own pose-cycle state.
        // A change here = the player pressed /cpose. This must be detected BEFORE the
        // timeline→emoteId heuristic, because standing poses each map to a DISTINCT emoteId
        // (107, 108, ...) and would otherwise misclassify as separate one-shot emotes,
        // bypassing pose replication. We capture the transition timeline (the _start tmb) so
        // the receiver can blend. Only fires after first observation (poseInit) to avoid a
        // spurious epoch bump on session start.
        // 255 (0xFF) is the game's "no pose family active" sentinel, not a real pose — seen
        // on standup/idle reset. Treat the byte as 0 (Idle) so a transition INTO 255 doesn't
        // register as a spurious cpose and never propagates the garbage sentinel downstream.
        const byte NoPose = 0xFF;
        byte normPoseType = poseType == NoPose ? (byte)0 : poseType;

        bool poseChanged = poseInit && (cPoseState != lastCPoseState || normPoseType != lastPoseType);
        lastPoseType = normPoseType;
        lastCPoseState = cPoseState;
        poseInit = true;

        if (poseChanged && !modeChanged)
        {
            // S111: an emote fired FROM a held cpose also changes the pose bytes
            // (CPose→0), so this branch was claiming the frame and capturing the
            // EMOTE timeline (e.g. /wave 706) as a "pose intro" — early return, no
            // epoch bump, receiver never learns an emote happened ("completely
            // ignored" from alt stances; default armed stance sits at PoseType=255
            // so its bytes don't change and waves classified correctly). A pose-exit
            // whose timeline is a known one-shot emote is an EMOTE INTERRUPT — fall
            // through to the emote classifier below.
            bool emoteInterrupt = cPoseState == 0
                && emoteTimelineIds.Contains(currentTimeline);

            if (!emoteInterrupt)
            {
                // S75: do NOT bump emoteEpoch. PlayActionTimeline(intro, loop) handles
                // the transition AND the loop setup natively. Bumping emoteEpoch would
                // trigger ApplyEmoteState's one-shot replay, playing a competing timeline.
                currentTimelineId = currentTimeline;
                // S107: remember this pose's intro. When a one-shot emote interrupts a held
                // cpose, the game reassumes by replaying THIS intro — the sustain branch
                // recognizes it by identity and re-streams the pose to the receiver.
                lastPoseIntro = cPoseState > 0 ? currentTimeline : (ushort)0;
                Dbg("[HMSync][POSEDIAG] cpose change pose=" + normPoseType + " cpose=" + cPoseState +
                    " tl=" + currentTimeline + " mode=" + currentMode + " emoteId=" + currentEmoteId +
                    " (no epoch bump)");
                lastTimelineId = currentTimeline;
                return;
            }

            Dbg("[HMSync][POSEDIAG] emote interrupt from cpose: tl=" +
                currentTimeline + " → emote classifier");
        }

        // ── S55: standup detection ──
        // Detects the get-up timeline appearing when the character exits a seated
        // mode. Two cases:
        //  (a) Early: AT[0] changes to the get-up timeline while mode is still seated
        //      (ideal — fires before mode change, gives receiver ~0.5s head start).
        //  (b) Simultaneous: AT[0] and CharMode both change in the same 10Hz tick
        //      (observed in practice — the game holds the sit-loop on AT[0] until the
        //      mode releases, so both flip together). In this case lastMode was seated,
        //      currentMode is Normal, and currentTimeline is the get-up timeline.
        // In both cases we set the standup signal. We do NOT return — if the mode also
        // changed this tick, the mode-change handler below must still run (set emoteId=0,
        // bump emoteEpoch, clear standupFired).
        bool wasSeated = lastMode == CharacterModes.InPositionLoop
            || lastMode == CharacterModes.EmoteLoop;
        bool isSeated = currentMode == CharacterModes.InPositionLoop
            || currentMode == CharacterModes.EmoteLoop;

        if (timelineChanged
            && (isSeated || (modeChanged && wasSeated))
            && standupTimelines.Contains(currentTimeline)
            && !standupFired)
        {
            standupTimelineId = currentTimeline;
            standupEpoch++;
            standupFired = true;
            lastTimelineId = currentTimeline;
            Dbg("[HMSync] Standup signal: TL " + currentTimeline +
                " param=" + (modeChanged ? lastModeParam : currentParam) +
                " epoch=" + standupEpoch +
                (modeChanged ? " (simultaneous)" : " (early)"));
            // Fall through — mode-change handler below needs to run if modeChanged.
        }

        if (modeChanged)
        {
            if (currentMode == CharacterModes.Normal && lastMode != CharacterModes.Normal)
            {
                // Mode reached Normal — the standup is complete. Clear the fire gate
                // so the next standup can trigger. Do NOT clear standupTimelineId here:
                // in the simultaneous case, the standup detection just set it this tick
                // and Detect() returns it at the end — clearing it would send TL=0 to
                // the receiver. The receiver only reacts on epoch change, so a stale
                // value on subsequent ticks is harmless.
                standupFired = false;

                // A one-shot interrupting a loop lands here: the /hms breaker clears the loop (Mode→Normal)
                // and PlayTimeline puts the one-shot in TimelineIds[0] in the SAME frame, so this tick sees
                // BOTH the mode drop and a real emote timeline. If we treated it as a bare standup we'd record
                // lastTimelineId=<emote> and the mutually-exclusive one-shot branch below would never fire —
                // the emote would be swallowed and the peer would never see the interrupt (the loop→non-loop
                // bug). So when the new timeline is a real emote, broadcast THAT emote instead. A genuine
                // standup (idle/locomotion timeline) is not in emoteTimelineIds and still routes to emoteId=0.
                // v0.7.414 — A STANDUP TIMELINE IS NEVER AN EMOTE.
                // The note above assumes "a genuine standup is not in emoteTimelineIds". FALSE for
                // chair/ground: 644 and 655 ARE emote timelines — they are ActionTimeline[0] of emote 51
                // and 53, the EndEmote halves of the sit. So a standup fell through to this branch and
                // was broadcast as emote 51. Emote 51's EmoteMode is 2, whose ConditionMode is
                // InPositionLoop, so the RECEIVER did SetMode(InPositionLoop, 2) and SAT THE PUPPET BACK
                // DOWN — the standup channel and the emote channel firing with opposite effects on the
                // same tick, emote winning. Observed:
                //     A: Standup signal: TL 644 param=2 epoch=1
                //     A: One-shot interrupting loop: ... tl=644 -> emoteId=51 epoch=1
                //     B: Emote 51 mode=InPositionLoop loop=644 on <A>
                //
                // 51/53 are exactly the EndEmote-only rows already filtered out of the emote catalogue
                // (v0.7.389) because they are transition halves, not player-invocable emotes. The same
                // rule has to hold on the wire: if the timeline is a standup, it belongs to the standup
                // channel and nothing else. This also fixes the ORGANIC in-session chair standup, which
                // had the identical collision.
                if (!standupTimelines.Contains(currentTimeline)
                    && emoteTimelineIds.Contains(currentTimeline)
                    && timelineToEmoteId.TryGetValue(currentTimeline, out var interruptId) && interruptId > 0)
                {
                    currentEmoteId = interruptId;
                    currentTimelineId = 0;
                    emoteEpoch++;
                    Dbg("[HMSync] One-shot interrupting loop: " + lastMode + "/" + lastModeParam +
                        " → Normal, tl=" + currentTimeline + " → emoteId=" + interruptId +
                        " epoch=" + emoteEpoch);
                }
                else
                {
                    currentEmoteId = 0;
                    currentTimelineId = currentTimeline;
                    emoteEndCooldownUntil = now + 1500;
                    emoteEpoch++;
                    Dbg("[HMSync] Mode: " + lastMode + "/" + lastModeParam +
                        " → Normal (standup) tl=" + currentTimeline + " epoch=" + emoteEpoch);
                }
            }
            else if (currentParam > 0)
            {
                // Persistent emote start
                emoteModeToEmoteId.TryGetValue(currentParam, out var emoteId);
                currentEmoteId = emoteId;
                currentTimelineId = 0;
                emoteEpoch++;
                Dbg("[HMSync] Mode: " + lastMode + "/" + lastModeParam +
                    " → " + currentMode + "/" + currentParam +
                    " emoteId=" + emoteId + " epoch=" + emoteEpoch);
            }

            lastMode = currentMode;
            lastModeParam = currentParam;
            lastTimelineId = currentTimeline;
        }
        else if (timelineChanged && now < emoteEndCooldownUntil)
        {
            // During standup cooldown, suppress non-emote timeline noise
            // (idle cycles, transition artifacts) but let real emotes through.
            // v0.7.414: standup timelines excluded — 644/655 are emote 51/53's AT[0] and would be
            // broadcast as an emote whose ConditionMode re-seats the peer. See the note above.
            if (!standupTimelines.Contains(currentTimeline)
                && emoteTimelineIds.Contains(currentTimeline))
            {
                timelineToEmoteId.TryGetValue(currentTimeline, out var emoteId);
                if (emoteId > 0)
                {
                    currentEmoteId = emoteId;
                    currentTimelineId = 0;
                    emoteEpoch++;
                    emoteEndCooldownUntil = 0; // real emote takes priority over cooldown
                    Dbg("[HMSync] One-shot (during standup): timeline=" + currentTimeline +
                        " → emoteId=" + emoteId + " epoch=" + emoteEpoch);
                }
            }
            lastTimelineId = currentTimeline;
        }
        else if (timelineChanged && currentMode == CharacterModes.Normal)
        {
            // S92: weapon-drawn cpose is a SUSTAINED state, not a one-shot. When the
            // weapon is drawn and we're in a cpose (PoseType=1, CPose>0), carry the
            // held timeline (3127/3128) on the wire so the receiver's AnimLock can hold
            // it. The one-shot path below would zero it (route through emote channel),
            // which is why the puppet reverted right after the cpose anim — the held
            // value never reached the receiver.
            // S107: identity-only sustain for held cposes (ALL standing families).
            // Replaces the loose S92 weapon guard, which sustained ANY timeline while
            // armed+cpose>0 — a one-shot emote played in that state (/wave) leaked its
            // timeline onto the wire as a "held pose" (the +1-leak class V warned of:
            // /panic→/point). Sustain ONLY:
            //  (a) the literal +1 flip of the streamed value (intro→loop), or
            //  (b) the remembered pose intro re-appearing — the game replays the intro
            //      when reassuming a held cpose after a one-shot emote finishes.
            // Anything else (a real emote) falls through to the one-shot path.
            if (cPoseState > 0
                && ((currentTimelineId > 0 && currentTimeline == (ushort)(currentTimelineId + 1))
                    || (lastPoseIntro > 0 && currentTimeline == lastPoseIntro)))
            {
                currentTimelineId = currentTimeline;
                Dbg("[HMSync] Cpose sustain: timeline=" + currentTimeline);
                lastTimelineId = currentTimeline;
            }
            // One-shot emote
            // v0.7.414: standup timelines excluded — same reason: they belong to the standup channel.
            else if (!standupTimelines.Contains(currentTimeline)
                && emoteTimelineIds.Contains(currentTimeline))
            {
                timelineToEmoteId.TryGetValue(currentTimeline, out var emoteId);
                if (emoteId > 0)
                {
                    currentEmoteId = emoteId;
                    currentTimelineId = 0;
                    emoteEpoch++;
                    Dbg("[HMSync] One-shot: timeline=" + currentTimeline +
                        " → emoteId=" + emoteId + " epoch=" + emoteEpoch);
                }
                lastTimelineId = currentTimeline;
            }
            else
            {
                lastTimelineId = currentTimeline;
            }
        }
        else if (timelineChanged && currentMode != CharacterModes.Normal)
        {
            // S112: identity discipline for the seated fallback. The intro→loop flip
            // of a seated cpose (or its intro re-appearing after an emote) is a POSE
            // CHANNEL sustain — stream it on TimelineId WITHOUT an epoch bump, so the
            // receiver's seated hold follows it like every other family. Everything
            // else (seated emotes, noise) keeps the original epoch-bump behavior.
            bool seatedPoseSustain =
                (currentTimelineId > 0 && currentTimeline == (ushort)(currentTimelineId + 1))
                || (lastPoseIntro > 0 && currentTimeline == lastPoseIntro);

            if (seatedPoseSustain)
            {
                currentTimelineId = currentTimeline;
                Dbg("[HMSync] Seated cpose sustain: timeline=" + currentTimeline);
                lastTimelineId = currentTimeline;
            }
            else
            {
                // Seated cpose fallback (timeline changed but pose byte didn't register a delta)
                currentTimelineId = currentTimeline;
                emoteEpoch++;
                Dbg("[HMSync] Pose change (tl): timeline=" + currentTimeline +
                    " epoch=" + emoteEpoch);
                lastTimelineId = currentTimeline;
            }
        }
        else if (currentTimeline != lastTimelineId)
        {
            lastTimelineId = currentTimeline;
        }
    }

    private void BuildLookupTables()
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<Emote>();
            if (sheet == null) return;

            foreach (var row in sheet)
            {
                if (row.RowId == 0 || row.RowId > ushort.MaxValue) continue;

                var emoteId = (ushort)row.RowId;

                for (int i = 0; i < row.ActionTimeline.Count; i++)
                {
                    var tlId = (ushort)row.ActionTimeline[i].RowId;
                    if (tlId > 0)
                    {
                        emoteTimelineIds.Add(tlId);
                        timelineToEmoteId.TryAdd(tlId, emoteId);
                    }
                }

                if (row.EmoteMode.RowId > 0 && row.EmoteMode.RowId <= byte.MaxValue)
                    emoteModeToEmoteId.TryAdd((byte)row.EmoteMode.RowId, emoteId);
            }

            // S55: build modeParam → standup timeline from EmoteMode sheet.
            // Each EmoteMode row with a non-zero EndEmote has a dedicated get-up emote;
            // that emote's AT[0] is the standup timeline for that mode param.
            var emoteModeSheet = dataManager.GetExcelSheet<EmoteMode>();
            if (emoteModeSheet != null)
            {
                foreach (var modeRow in emoteModeSheet)
                {
                    if (modeRow.RowId == 0 || modeRow.RowId > byte.MaxValue) continue;
                    var endEmoteRowId = modeRow.EndEmote.RowId;
                    if (endEmoteRowId == 0) continue;
                    try
                    {
                        var endEmote = sheet.GetRow(endEmoteRowId);
                        var standupTl = (ushort)endEmote.ActionTimeline[0].RowId;
                        if (standupTl > 0)
                        {
                            modeParamToStandupTimeline[(byte)modeRow.RowId] = standupTl;
                            standupTimelines.Add(standupTl);
                        }
                    }
                    catch { /* EndEmote row not found — skip */ }
                }
            }

            log.Information("[HMSync] Emote tables: " +
                timelineToEmoteId.Count + " timeline mappings, " +
                emoteModeToEmoteId.Count + " mode mappings, " +
                standupTimelines.Count + " standup timelines");
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] Failed to build emote tables: " + ex.Message);
        }
    }
}
