using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace HMSync.Services;

// v0.7.357 DIAGNOSTIC (/hms gposemount): why do HMS-applied mounts flicker out a few frames after entering gpose?
//
// The symptom (mount persists into gpose, then vanishes after a few frames) is consistent with TWO opposite causes,
// which need OPPOSITE fixes - so this probe distinguishes them before any fix is written:
//
//   (A) HMS clears it.       Some HMS path calls CreateAndSetupMount(0) when gpose engages (e.g. gpose reshuffles the
//                            object table → a per-frame index resolve mistakes a peer for "departed" → departure
//                            teardown fires, which dismounts). FIX = gate that path on IsGPosing.
//   (B) The game rebuilds it. GPose clones actors and re-initialises them from SERVER-authoritative state. An HMS mount
//                            is synthetic (the server sees us unmounted behind the firewall), so the rebuild drops it.
//                            FIX = re-assert the mount after gpose finishes its setup.
//   (C) Neither - state stays, rendering stops. MountId remains non-zero but nothing draws → a draw/visibility problem
//                            on the gpose actor copy, not a state problem.
//
// The probe logs, every frame while armed: IsGPosing, and for the local player + each bound peer the live
// Mount.MountId / Mode / whether a MountObject exists. StateApplyService additionally logs every CreateAndSetupMount
// call it makes while the probe is armed (see MountClearLog). Read the frames around gpose entry:
//   MountId → 0 WITH an HMS clear logged at that frame  => (A)
//   MountId → 0 WITHOUT any HMS clear                   => (B)
//   MountId stays non-zero but the mount is invisible    => (C)
public sealed unsafe class GPoseMountProbe
{
    private readonly IPluginLog log;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly Func<System.Collections.Generic.IEnumerable<uint>> getPeerIndices;

    private bool armed;
    private int frame;
    private bool lastGPosing;
    private ushort lastSelfMount;
    private string lastSelfSig = "";   // v0.7.359: change-detect signature so the de-draw transition always logs

    public GPoseMountProbe(IPluginLog log, IClientState clientState, IObjectTable objectTable,
        Func<System.Collections.Generic.IEnumerable<uint>> getPeerIndices)
    {
        this.log = log;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.getPeerIndices = getPeerIndices;
    }

    /// <summary>True while the probe is armed - StateApplyService checks this to log its own clear-calls.</summary>
    public bool Armed => armed;

    public void Toggle()
    {
        armed = !armed;
        frame = 0;
        lastGPosing = clientState.IsGPosing;
        log.Information("[HMSync] [GPOSEMOUNT] probe " + (armed ? "ARMED - now enter gpose while mounted." : "disarmed."));
    }

    /// <summary>Called by StateApplyService (and any other clear-site) right before it clears a mount, so the log
    /// shows whether an HMS call coincides with the mount disappearing.</summary>
    public void NoteClear(string where, uint objectIndex, ushort hadMountId)
    {
        if (!armed) return;
        log.Information("[HMSync] [GPOSEMOUNT] f" + frame + " *** HMS CLEARED MOUNT *** at " + where +
            " objIdx=" + objectIndex + " hadMountId=" + hadMountId + " (gposing=" + clientState.IsGPosing + ")");
    }

    public void Update()
    {
        if (!armed) return;
        bool gp = clientState.IsGPosing;
        if (gp != lastGPosing)
        {
            log.Information("[HMSync] [GPOSEMOUNT] f" + frame + " === GPOSE " + (gp ? "ENTERED" : "EXITED") + " ===");
            lastGPosing = gp;
            frame = 0;   // restart the frame counter at the transition so the log reads f0,f1,f2… from entry
        }

        // local player
        var lp = Control.GetLocalPlayer();
        if (lp != null)
        {
            var self = (Character*)lp;
            ushort mid = self->Mount.MountId;
            bool hasObj = self->Mount.MountObject != null;
            // v0.7.359: log CONTINUOUSLY while armed (the v0.7.358 30-frame cap put the actual de-draw moment in a
            // blind spot - it happens ~0.6s after gpose entry, well past frame 29). To keep the log readable we print
            // every frame for the first 30, then only when something CHANGES (draw state, ready state, render flags,
            // mount id) - so the de-draw transition is guaranteed to appear.
            string sig = "none";
            if (hasObj)
            {
                var mgo = &self->Mount.MountObject->GameObject;
                var rgo = (GameObject*)lp;   // the RIDER's own game object
                // v0.7.359: track the RIDER's draw state too. Outside gpose the mount showed RenderFlags=0x8900 /
                // DrawObject=NO yet rendered fine - consistent with the mount being drawn via its attachment to the
                // rider rather than as an independent object. If gpose swaps or rebuilds the RIDER's DrawObject, that
                // attachment breaks and the mount de-draws - which would explain why EnableDraw on the mount alone
                // never sticks. Logging both lets us see which one changes at the de-draw moment.
                sig = mid + "|" + ((uint)mgo->RenderFlags).ToString("X") + "|" + mgo->IsReadyToDraw() + "|" + (mgo->DrawObject != null)
                    + "|R" + ((uint)rgo->RenderFlags).ToString("X") + "|" + (rgo->DrawObject != null);
                if (frame < 30 || sig != lastSelfSig)
                {
                    log.Information("[HMSync] [GPOSEMOUNT] f" + frame + " gposing=" + gp +
                        " SELF mountId=" + mid + " mode=" + self->Mode +
                        " | MOUNT rf=0x" + ((uint)mgo->RenderFlags).ToString("X") +
                        " ready=" + mgo->IsReadyToDraw() +
                        " draw=" + (mgo->DrawObject != null ? "yes" : "NO") +
                        " | RIDER rf=0x" + ((uint)rgo->RenderFlags).ToString("X") +
                        " draw=" + (rgo->DrawObject != null ? "yes" : "NO") +
                        (sig != lastSelfSig && frame >= 30 ? "   <<< CHANGED" : ""));
                }
            }
            else
            {
                sig = mid + "|noobj";
                if (frame < 30 || sig != lastSelfSig)
                    log.Information("[HMSync] [GPOSEMOUNT] f" + frame + " gposing=" + gp +
                        " SELF mountId=" + mid + " mode=" + self->Mode + " mountObj=NO" +
                        (sig != lastSelfSig && frame >= 30 ? "   <<< CHANGED" : ""));
            }
            lastSelfSig = sig;
            lastSelfMount = mid;
        }

        // peers (first few frames only - enough to see the transition without flooding)
        if (frame < 30)
        {
            foreach (var idx in getPeerIndices())
            {
                try
                {
                    var obj = objectTable[(int)idx];
                    if (obj == null) { log.Information("[HMSync] [GPOSEMOUNT] f" + frame + " PEER idx=" + idx + " OBJECT NULL"); continue; }
                    var c = (Character*)obj.Address;
                    log.Information("[HMSync] [GPOSEMOUNT] f" + frame + " PEER idx=" + idx +
                        " mountId=" + c->Mount.MountId + " mode=" + c->Mode +
                        " mountObj=" + (c->Mount.MountObject != null ? "yes" : "NO"));
                }
                catch (Exception ex) { log.Information("[HMSync] [GPOSEMOUNT] peer idx=" + idx + " read failed: " + ex.Message); }
            }
        }

        frame++;
    }
}
