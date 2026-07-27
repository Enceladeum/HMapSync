using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Lumina.Excel.Sheets;

namespace HMSync.Services;

/// <summary>
/// Diagnostic service. /hms diag logs per-frame animation state changes.
/// Tracks: Mode, ModeParam, BaseOverride, ALL 14 TimelineIds slots, IsWeaponDrawn, pose/cpose,
/// and the DrawData cosmetic bits (visor/hat/weapon-hidden). Change-gated: one line per state change.
/// </summary>
public unsafe class EmoteDiagService : IDisposable
{
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private bool logging;
    private bool peerMode;
    private long logUntil;

    // Previous frame state for change detection
    private CharacterModes prevMode;
    private byte prevParam;
    private ushort prevBaseOverride;
    private bool prevWeaponDrawn;
    private byte prevPoseType;
    private byte prevCPoseState;
    private bool prevWeaponHidden;
    private bool prevHatHidden;
    private bool prevVisorToggled;
    private float prevDrawOffsetY;
    private float prevPosY;

    // v0.7.381 — full sequencer coverage.
    // TimelineIds is FixedSizeArray14<ushort> on ActionTimelineSequencer @0xE0: "the timeline active
    // in each slot or 0 when none". Names are from that struct's own remarks; 4-6 are documented as
    // unknown purpose, so they're printed by index only.
    private const int TlSlots = 14;
    private readonly ushort[] prevSlots = new ushort[TlSlots];
    private ulong prevStateFlags;

    private static string SlotName(int i) => i switch
    {
        0 => "Base", 1 => "Upper", 2 => "Face", 3 => "Add",
        7 => "Lips", 8 => "Part1", 9 => "Part2", 10 => "Part3", 11 => "Part4", 12 => "Ovrl",
        _ => "slot",
    };

    public EmoteDiagService(IObjectTable objectTable, IFramework framework, IDataManager dataManager, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.framework = framework;
        this.dataManager = dataManager;
        this.log = log;
    }

    public void StartLogging(int durationMs = 15000, bool peer = false)
    {
        if (logging)
        {
            // Already running — extend the timer
            logUntil = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + durationMs;
            log.Information("[HMSync-DIAG] Extended — logging for " + durationMs + "ms more.");
            return;
        }

        logging = true;
        peerMode = peer;
        logUntil = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + durationMs;
        prevMode = CharacterModes.None;
        prevParam = 255;
        // v0.7.381: seed every slot to a sentinel so the FIRST sampled frame always logs a full
        // baseline. Previously only prevTl0 was seeded; with the whole array compared, an unseeded
        // array of zeros would suppress the baseline line whenever the character started idle.
        for (int i = 0; i < TlSlots; i++) prevSlots[i] = ushort.MaxValue;
        prevBaseOverride = ushort.MaxValue;
        prevStateFlags = ulong.MaxValue;
        prevWeaponDrawn = false;
        framework.Update += OnUpdate;
        log.Information("[HMSync-DIAG] Started (" + (peer ? "PEER" : "LOCAL") +
            ") — logging for " + (durationMs / 1000) + "s.");
    }

    public void StopLogging()
    {
        logging = false;
        framework.Update -= OnUpdate;
        log.Information("[HMSync-DIAG] Stopped.");
    }

    private void OnUpdate(IFramework fw)
    {
        if (!logging) return;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > logUntil)
        {
            StopLogging();
            return;
        }

        Dalamud.Game.ClientState.Objects.Types.IGameObject? target;
        if (peerMode)
        {
            target = null;
            var localId = objectTable.LocalPlayer?.GameObjectId ?? 0;
            foreach (var obj in objectTable)
            {
                var on = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
                if (on == null) continue;
                if (on->ObjectKind == FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.Pc
                    && obj.GameObjectId != localId)
                {
                    target = obj;
                    break;
                }
            }
            if (target == null) return; // no peer present yet
        }
        else
        {
            target = objectTable.LocalPlayer;
            if (target == null) return;
        }

        var c = (Character*)target.Address;
        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target.Address;
        var mode = c->Mode;
        var param = c->ModeParam;
        var baseOv = c->Timeline.BaseOverride;
        // ANIM_2_009 (v0.7.381): sample ALL 14 sequencer slots, not just 0-2. TimelineIds is
        // FixedSizeArray14<ushort> ("the timeline active in each slot or 0 when none"), and
        // PlayTimeline "determines which slot the timeline belongs in" — so an animation we're
        // trying to identify can land anywhere. Watching only 0-2 was enough for emote/pose work
        // but silently misses Add(3), Lips(7), Parts1-4(8-11) and Overlay(12). The visor travel is
        // the case in point: we do NOT know which slot it uses, and guessing from the sheet's Slot
        // column is unsafe (battle/idle=34 reads Slot=2 there yet behaves as a base-lane evictor).
        var slots = new ushort[TlSlots];
        for (int i = 0; i < TlSlots; i++)
            slots[i] = (ushort)c->Timeline.TimelineSequencer.TimelineIds[i];

        // ANIM_2_009 (v0.7.383): the visor lives on the DRAW OBJECT, not the Character.
        // Proven by elimination: /hms diag showed no ActionTimeline slot moving and
        // DrawData.IsVisorToggled never flipping, and a DrawDataContainer byte-diff showed ZERO
        // bytes changing across a toggle. FFXIVClientStructs puts it on CharacterBase.StateFlags
        // @0x90 (the DrawObject):
        //     VisorToggled  = 1UL << 6   — the state (confirmed live)
        //     VisorChanging = 1UL << 7   — DOES NOT MATCH THIS PATCH. See below.
        //
        // ⚠ OBSERVED, AND IT CONTRADICTS THE REFERENCE: the changing/trigger flag on this patch is
        // BIT 30, not bit 7. Bit 7 never moved in any capture; bit 30 pulses for exactly one frame
        // (10–15 ms) on every toggle. Twelve transitions across two characters, sender and peer.
        // Decoded below as *CHANGING* using bit 30. Re-verify if CS is updated.
        //
        // What the visor actually IS (v0.7.386 finding, from Penumbra's GMP meta editor): a GIMMICK
        // PARAMETER entry keyed by head model id — {Enabled, Animated, RotationA/B/C degrees} in
        // chara/xls/equipmentparameter/gimmickparameter.gmp. Not an ActionTimeline at all, which is
        // why no sequencer slot ever moves. The "blend" is the game interpolating the visor bone.
        // Also sampled: HasUmbrella (1<<16), VieraEarsHidden (1<<31), VieraEarsChanging (1<<32).
        ulong stateFlags = 0;
        bool haveDrawObj = false;
        {
            var drawObj = native->DrawObject;
            if (drawObj != null)
            {
                haveDrawObj = true;
                stateFlags = (ulong)((CharacterBase*)drawObj)->StateFlags;
            }
        }
        var weaponDrawn = c->Timeline.IsWeaponDrawn;
        var poseType = (byte)c->EmoteController.CurrentPoseType;
        var cPoseState = c->EmoteController.CPoseState;
        var weaponHidden = c->DrawData.IsWeaponHidden;
        var hatHidden = c->DrawData.IsHatHidden;
        var visorToggled = c->DrawData.IsVisorToggled;
        var drawOffsetY = native->DrawOffset.Y;
        var posY = native->Position.Y;
        var height = native->Height;

        // v0.7.381: ANY slot changing is a change — previously only tl0 was compared, so a timeline
        // that landed in slot 3+ never triggered a log line and was invisible to this diagnostic.
        bool anySlotChanged = false;
        for (int i = 0; i < TlSlots; i++)
            if (slots[i] != prevSlots[i]) { anySlotChanged = true; break; }

        bool changed = mode != prevMode || param != prevParam || anySlotChanged
            || baseOv != prevBaseOverride || weaponDrawn != prevWeaponDrawn
            || weaponHidden != prevWeaponHidden || hatHidden != prevHatHidden
            || visorToggled != prevVisorToggled || stateFlags != prevStateFlags
            || poseType != prevPoseType || cPoseState != prevCPoseState
            || MathF.Abs(drawOffsetY - prevDrawOffsetY) > 0.01f
            || MathF.Abs(posY - prevPosY) > 0.05f;

        if (changed)
        {
            // Emote sheet lookup for persistent emotes
            var emoteInfo = "";
            if (param > 0)
            {
                try
                {
                    var emoteSheet = dataManager.GetExcelSheet<Emote>();
                    if (emoteSheet != null)
                    {
                        foreach (var row in emoteSheet)
                        {
                            if (row.EmoteMode.RowId == param)
                            {
                                emoteInfo = " emote=" + row.RowId + " AT[";
                                for (int i = 0; i < row.ActionTimeline.Count && i < 7; i++)
                                {
                                    if (i > 0) emoteInfo += ",";
                                    emoteInfo += row.ActionTimeline[i].RowId;
                                }
                                emoteInfo += "]";
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex) { log.Debug("[HMSync] Diag sample failed: " + ex.Message); }
            }

            // Mark what changed
            var delta = "";
            if (mode != prevMode || param != prevParam) delta += " [MODE]";
            if (anySlotChanged) delta += " [TL]";
            if (baseOv != prevBaseOverride) delta += " [BASEOV]";
            if (weaponDrawn != prevWeaponDrawn) delta += " [WEAPON]";
            if (weaponHidden != prevWeaponHidden) delta += " [DISPLAYARMS]";
            if (hatHidden != prevHatHidden) delta += " [HAT]";
            if (visorToggled != prevVisorToggled) delta += " [VISOR]";
            if (MathF.Abs(drawOffsetY - prevDrawOffsetY) > 0.01f) delta += " [DRAWOFS]";
            if (MathF.Abs(posY - prevPosY) > 0.05f) delta += " [POSY]";
            if (poseType != prevPoseType || cPoseState != prevCPoseState) delta += " [POSE]";
            if (stateFlags != prevStateFlags) delta += " [MODELFLAGS]";

            // v0.7.383: decode CharacterBase.StateFlags. VisorChanging is the animation-in-progress
            // flag — if it pulses on a toggle, that pulse IS the broadcastable event.
            string modelFlags;
            if (!haveDrawObj) modelFlags = "(no DrawObject)";
            else
            {
                modelFlags = "0x" + stateFlags.ToString("X") +
                    (((stateFlags >> 6) & 1) != 0 ? " VISOR-ON" : " visor-off") +
                    (((stateFlags >> 30) & 1) != 0 ? " *CHANGING*" : "");
                if (((stateFlags >> 16) & 1) != 0) modelFlags += " umbrella";
                if (((stateFlags >> 31) & 1) != 0) modelFlags += " viera-hidden";
                if (((stateFlags >> 32) & 1) != 0) modelFlags += " viera-changing";
            }

            // v0.7.381: print EVERY occupied slot, with a * on any that changed this frame, and
            // resolve the ActionTimeline Key so the log reads as animation names not bare numbers.
            // Slot names per ActionTimelineSequencer's own remarks (FFXIVClientStructs).
            var tlMap = "";
            for (int i = 0; i < TlSlots; i++)
            {
                if (slots[i] == 0 && prevSlots[i] == 0) continue;   // never-occupied slots stay quiet
                tlMap += " " + SlotName(i) + "[" + i + "]=" + slots[i];
                if (slots[i] != prevSlots[i]) tlMap += "*";
            }

            log.Information("[HMSync-DIAG]" + delta +
                " Mode=" + mode + "/" + param +
                " BaseOv=" + baseOv +
                " TL:" + tlMap +
                " Pose=" + poseType + "/" + cPoseState +
                " Weapon=" + weaponDrawn +
                " WpnHide=" + weaponHidden +
                " HatHide=" + hatHidden +
                " Visor=" + visorToggled +
                " Model=" + modelFlags +
                " DrawOfsY=" + drawOffsetY.ToString("F3") +
                " PosY=" + posY.ToString("F3") +
                " Height=" + height.ToString("F3") +
                emoteInfo);

            prevMode = mode;
            prevParam = param;
            System.Array.Copy(slots, prevSlots, TlSlots);
            prevStateFlags = stateFlags;
            prevBaseOverride = baseOv;
            prevWeaponDrawn = weaponDrawn;
            prevPoseType = poseType;
            prevCPoseState = cPoseState;
            prevWeaponHidden = weaponHidden;
            prevHatHidden = hatHidden;
            prevVisorToggled = visorToggled;
            prevDrawOffsetY = drawOffsetY;
            prevPosY = posY;
        }
    }

    public void Dispose()
    {
        if (logging) StopLogging();
    }
}
