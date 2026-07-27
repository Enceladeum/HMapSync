using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace HMSync.Services;

// v0.7.360: keep HMS-applied mounts VISIBLE in gpose — the RECOVERY half of the fix.
//
// Root cause (found via /hms gposemount): ActorVisibilityService's 30-frame hide sweep hides every ObjectKind.Pc that
// isn't the LIVE local player index or a known peer. GPose creates COPIES of actors at DIFFERENT object indices, so
// the player's own gpose copy fell through that guard and got RenderFlags |= 0x02 — and its mount inherited the hide.
// The probe caught it precisely: at the first sweep after gpose entry (frame 36), RIDER rf 0x0 → 0x1002 and MOUNT
// rf 0x0 → 0x8802 on the same frame. Nothing destroyed the mount; it was hidden.
//
// The PRIMARY fix is in ActorVisibilityService: the sweep now stands down while IsGPosing. This service is the
// recovery pass for the edge case where a sweep landed immediately before/at gpose entry — while gposing it clears
// the 0x02 bit from actors HMS ITSELF HID, so anything we hid comes back.
//
// v0.7.391 CORRECTION. The original note claimed "clearing only ever removes a bit we set (0x00 = visible is these
// objects' default)". THAT IS FALSE, and /hms gposediag caught it: at the exact millisecond gpose spawns its clones
// at indices 201/202, THE GAME sets 0x02 on the ORIGINALS — that is how gpose works, you are meant to see the clone
// and not the original. This pass was blanket-clearing every Pc's 0x02, so it undid the game's own hide and BOTH
// copies drew. That is the "peers appear twice in gpose" bug, and it was caused by this recovery pass.
// Trace: `hidden=[]` (HMS had hidden nothing) yet the pass still cleared idx 0 and idx 2.
// Fix: only clear a hide WE applied, which is what the comment always claimed and the code never checked.
public sealed unsafe class GPoseMountDrawService
{
    private const uint InvisibleFlag = 0x02;

    private readonly IPluginLog log;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly Func<bool> isSessionActive;
    private readonly Func<System.Collections.Generic.IEnumerable<ushort>> getPeerIndices;
    // v0.7.391: "did HMS hide this object index?" — the ownership test this pass always needed.
    private readonly Func<ushort, bool> wasHiddenByUs;

    private bool lastGPosing;
    private int unhidThisEntry;

    public GPoseMountDrawService(IPluginLog log, IClientState clientState, IObjectTable objectTable,
        Func<bool> isSessionActive, Func<System.Collections.Generic.IEnumerable<ushort>> getPeerIndices,
        Func<ushort, bool> wasHiddenByUs)
    {
        this.wasHiddenByUs = wasHiddenByUs;
        this.log = log;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.isSessionActive = isSessionActive;
        this.getPeerIndices = getPeerIndices;
    }

    public void Update()
    {
        bool gp = clientState.IsGPosing;
        if (gp != lastGPosing)
        {
            lastGPosing = gp;
            unhidThisEntry = 0;
        }
        if (!gp) return;
        if (!isSessionActive()) return;

        // While gposing, un-hide any Pc actor (and its mount) still carrying our 0x02 hide bit.
        foreach (var obj in objectTable)
        {
            try
            {
                var native = (GameObject*)obj.Address;
                if (native == null) continue;
                if (native->ObjectKind != ObjectKind.Pc) continue;
                // v0.7.391: ONLY un-hide what HMS hid. GPose legitimately hides originals when it spawns
                // its clones; clearing that is what produced the duplicate.
                if (!wasHiddenByUs((ushort)obj.ObjectIndex)) continue;

                bool touched = false;
                if ((native->RenderFlags & (VisibilityFlags)InvisibleFlag) != 0)
                {
                    native->RenderFlags &= ~(VisibilityFlags)InvisibleFlag;
                    touched = true;
                }

                // its mount object carries the hide too
                var c = (Character*)obj.Address;
                if (c->Mount.MountId != 0 && c->Mount.MountObject != null)
                {
                    var mgo = &c->Mount.MountObject->GameObject;
                    if ((mgo->RenderFlags & (VisibilityFlags)InvisibleFlag) != 0)
                    {
                        mgo->RenderFlags &= ~(VisibilityFlags)InvisibleFlag;
                        touched = true;
                    }
                }

                if (touched && unhidThisEntry < 8)
                {
                    unhidThisEntry++;
                    log.Debug("[HMSync] [GPOSEMOUNT] cleared 0x02 hide in gpose on idx=" + obj.ObjectIndex + ".");
                }
            }
            catch { /* transient object-table churn — skip */ }
        }
    }
}
