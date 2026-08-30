namespace HMSync.Services;

using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using HMSync.Sync;
using HMSync.Wire;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
// DisguiseSyncService — the BRIDGE between HDM's IPC (HdmIpc) and the HMS relay wire (0x50/0x51/0x52).
//
// It owns the three concerns HdmIpc deliberately doesn't: (1) the relay wire (envelope stamping + send/receive),
// (2) IDENTITY resolution (SenderContentId ⇄ local objectIndex, via StateApplyService's peer roster), and
// (3) LIFECYCLE (first-sight replay from a per-source cache, peer-left revert/despawn, session reset).
//
// SENDER (this client is the DM):  HdmIpc raises C# events on change → we stamp a wire envelope (SubjectId,
//   SenderContentId) and put it on the relay. SubjectId "" = our own body; "<selfCid>:<slot>" = one of our puppets.
// RECEIVER (mirroring a remote DM): relay hands us a self-describing payload → we resolve the subject to a LOCAL
//   actor and drive HdmIpc's receiver methods. Own-body disguises target the peer's synced puppet body (needs the
//   peer in render range → cached + applied on OnPeerBound). Puppet subjects spawn a LOCAL mirror puppet we own.
//
// THREADING: HdmIpc's outbound events fire on the framework thread (HDM emits via SendMessage there), so SENDER
// handlers run on-thread and read the local ContentId directly. relay events fire on the RECEIVE thread, so all
// RECEIVER work is marshalled onto the framework thread before touching the object table / HDM IPC.
// See docs/HDM-sync-HMS-side-brief.md §4.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
public sealed class DisguiseSyncService : IDisposable
{
    private readonly HdmIpc hdm;
    private readonly RelaySyncService relay;
    private readonly StateApplyService stateApply;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Func<ulong> localContentId;

    // Our own ContentId, cached on the framework thread (sender path) so the receive-thread echo-suppress can read it
    // without touching the object table off-thread. 0 until the first sender emit / session start.
    private ulong cachedSelfCid;

    // ── RECEIVER state (framework-thread only) ──
    // Last own-body atom per remote source, so a disguise that arrived before the peer was in render range gets
    // applied the moment their body binds (OnPeerBound). Keyed by SenderContentId.
    private readonly Dictionary<ulong, DisguiseUpdatePayload> lastOwnBody = new();
    // Local mirror puppets we've spawned to represent a remote DM's puppets. Keyed by the wire SubjectId
    // ("<sourceCid>:<slot>") → our LOCAL object index.
    private readonly Dictionary<string, int> mirrorPuppets = new();
    // Freeze-anim (0x55): last-seen freeze bit per remote OWN body (keyed by SenderContentId), applied the moment the
    // peer binds (OnPeerBound) if it arrived early. Only "true" is cached-meaningful — a fresh bind is unfrozen by default.
    private readonly Dictionary<ulong, bool> lastFrozenOwnBody = new();
    // Freeze-anim last-seen bit per remote PUPPET subject ("<sourceCid>:<slot>"), applied on mirror spawn if the freeze
    // edge arrived before the puppet existed. Cleared when the owning peer departs.
    private readonly Dictionary<string, bool> lastFrozenPuppet = new();

    // ── SENDER state (framework-thread only) ──
    // Our OWN live puppets' slot → local object index, so the per-frame move handler (which only carries a slot) can
    // read the puppet's live resolved locomotion timeline to broadcast in PuppetMove.Anim. Populated on spawn,
    // dropped on despawn. HdmPuppetInfo carries ObjectIndex on spawn/broadcast; the move event does not, hence this map.
    private readonly Dictionary<int, int> localPuppets = new();

    public DisguiseSyncService(HdmIpc hdm, RelaySyncService relay, StateApplyService stateApply,
        IFramework framework, IPluginLog log, Func<ulong> localContentId)
    {
        this.hdm = hdm;
        this.relay = relay;
        this.stateApply = stateApply;
        this.framework = framework;
        this.log = log;
        this.localContentId = localContentId;

        // SENDER: HDM change signals → wire.
        hdm.OnDisguiseChanged += OnLocalDisguiseChanged;
        hdm.OnActionFired += OnLocalActionFired;
        hdm.OnPuppetSpawned += OnLocalPuppetSpawned;
        hdm.OnPuppetDespawned += OnLocalPuppetDespawned;
        hdm.OnPuppetMoved += OnLocalPuppetMoved;
        hdm.OnOwnBodyHidden += OnLocalOwnBodyHidden;   // Bug B: mirror the DM's own-body hide edge to peers
        hdm.OnFreezeChanged += OnLocalFrozen;          // Freeze-anim: mirror the DM's per-actor freeze edge to peers
        // HDM (re)appeared mid-session → re-offer our full current state so peers catch up.
        hdm.OnHdmReady += BroadcastFullState;

        // RECEIVER: wire → HDM drive.
        relay.OnDisguiseReceived += OnDisguiseReceived;
        relay.OnActionPulseReceived += OnActionPulseReceived;
        relay.OnPuppetMoveReceived += OnPuppetMoveReceived;
        relay.OnOwnBodyHiddenReceived += OnOwnBodyHiddenReceived;   // Bug B: suppress a remote DM's own body while hidden
        relay.OnFreezeReceived += OnFreezeReceived;                 // Freeze-anim: drive HDM SetFrozen on the mirrored actor
    }

    // ═══════════════════════════ SENDER (local DM → wire) ═══════════════════════════
    // All of these run on the framework thread (HDM emits there). Fire-and-forget the async socket write.

    private void OnLocalDisguiseChanged(int? slot, HdmDisguiseAtom atom)
    {
        var self = RefreshSelfCid();
        var subject = slot.HasValue ? PuppetSubject(self, slot.Value) : "";
        _ = relay.SendDisguiseUpdate(BuildDisguise(subject, self, atom));
    }

    private void OnLocalActionFired(int slot, uint playId)
    {
        var self = RefreshSelfCid();
        var subject = slot < 0 ? "" : PuppetSubject(self, slot);   // HDM own-body sentinel is -1 (scalar lane), not null
        _ = relay.SendActionPulse(new ActionPulsePayload
        {
            SubjectId = subject,
            SenderContentId = self,
            PlayId = (ushort)playId,
        });
    }

    private void OnLocalPuppetSpawned(HdmPuppetInfo info)
    {
        var self = RefreshSelfCid();
        var subject = PuppetSubject(self, info.Slot);
        localPuppets[info.Slot] = info.ObjectIndex;   // track for the per-frame move handler's Anim read
        // The disguise (spawn-or-apply on the receiver) and the initial pose ride two opcodes; send them in order.
        _ = relay.SendDisguiseUpdate(BuildDisguise(subject, self, info.Atom));
        _ = relay.SendPuppetMove(new PuppetMovePayload
        {
            SubjectId = subject, SenderContentId = self,
            X = info.Px, Y = info.Py, Z = info.Pz, Rot = info.Rot,
            Anim = stateApply.ReadLocomotionTimeline(info.ObjectIndex),
        });
        // Freeze-anim: if the puppet spawned already frozen, carry that bit so the mirror pins immediately (visible=default).
        if (info.Frozen)
            _ = relay.SendFreezeUpdate(new FreezeUpdatePayload { SubjectId = subject, SenderContentId = self, Frozen = true });
    }

    private void OnLocalPuppetDespawned(int slot)
    {
        var self = RefreshSelfCid();
        localPuppets.Remove(slot);
        // b189: despawn is now EXPLICIT (Despawn flag), not inferred from Kind==0 - a blank spawn and a guise-revert
        // are also Kind 0 on a puppet subject, and neither is a despawn. Only this path sets the flag.
        var payload = BuildDisguise(PuppetSubject(self, slot), self, new HdmDisguiseAtom { Kind = 0 });
        payload.Despawn = true;
        _ = relay.SendDisguiseUpdate(payload);
    }

    private void OnLocalPuppetMoved(int slot, float x, float y, float z, float rot)
    {
        var self = RefreshSelfCid();
        // Read the puppet's live resolved locomotion timeline so peers animate the mirror instead of sliding it.
        // slot→objectIndex from the spawn map; 0 (idle) if not yet tracked (self-corrects once the spawn registers).
        ushort anim = localPuppets.TryGetValue(slot, out var objIdx) ? stateApply.ReadLocomotionTimeline(objIdx) : (ushort)0;
        _ = relay.SendPuppetMove(new PuppetMovePayload
        {
            SubjectId = PuppetSubject(self, slot), SenderContentId = self,
            X = x, Y = y, Z = z, Rot = rot,
            Anim = anim,
        });
    }

    // Bug B: the DM's own body just became hidden/visible (possession start/release, or the "hide me while driving"
    // toggle). Put the edge on the wire so peers suppress/restore our own-body mirror. Deduped HDM-side (one per edge).
    private void OnLocalOwnBodyHidden(bool hidden)
    {
        var self = RefreshSelfCid();
        _ = relay.SendOwnBodyHidden(new OwnBodyHiddenPayload
        {
            SubjectId = "",
            SenderContentId = self,
            Hidden = hidden,
        });
    }

    // Freeze-anim: the DM toggled the animation-freeze checkbox for one actor (own body or a puppet). Put the per-subject
    // edge on the wire so peers pin/unpin the matching mirror's animation speed. slot < 0 = own body (scalar lane), like
    // OnLocalActionFired. Deduped HDM-side (one event per edge, per actor).
    private void OnLocalFrozen(int slot, bool frozen)
    {
        var self = RefreshSelfCid();
        var subject = slot < 0 ? "" : PuppetSubject(self, slot);
        _ = relay.SendFreezeUpdate(new FreezeUpdatePayload
        {
            SubjectId = subject,
            SenderContentId = self,
            Frozen = frozen,
        });
    }

    // Late-join / HDM-reappear: re-broadcast our current own-body disguise + every live puppet by PULLING from HDM
    // (its change events already fired before the newcomer was listening). Idempotent on existing peers (epoch-gated).
    public void BroadcastFullState()
    {
        if (!relay.IsConnected) return;
        _ = framework.RunOnFrameworkThread(() =>
        {
            var self = RefreshSelfCid();
            var own = hdm.GetDisguise();
            if (own != null && own.Kind != 0)
                _ = relay.SendDisguiseUpdate(BuildDisguise("", self, own));
            foreach (var p in hdm.GetPuppets())
            {
                var subject = PuppetSubject(self, p.Slot);
                localPuppets[p.Slot] = p.ObjectIndex;   // keep the sender map authoritative on re-broadcast
                _ = relay.SendDisguiseUpdate(BuildDisguise(subject, self, p.Atom));
                _ = relay.SendPuppetMove(new PuppetMovePayload
                {
                    SubjectId = subject, SenderContentId = self,
                    X = p.Px, Y = p.Py, Z = p.Pz, Rot = p.Rot,
                    Anim = stateApply.ReadLocomotionTimeline(p.ObjectIndex),
                });
                // Freeze-anim late-join: a puppet that's currently frozen carries its bit so the newcomer pins it on first-sight.
                if (p.Frozen)
                    _ = relay.SendFreezeUpdate(new FreezeUpdatePayload { SubjectId = subject, SenderContentId = self, Frozen = true });
            }
            // Bug B late-join: if we're mid-possession with our own body hidden, tell the newcomer so it renders us
            // suppressed on first-sight. Only re-send the "hidden" edge (visible is the default; no need to spam false).
            if (hdm.GetOwnBodyHidden())
                _ = relay.SendOwnBodyHidden(new OwnBodyHiddenPayload { SubjectId = "", SenderContentId = self, Hidden = true });
            // Freeze-anim late-join: if our own body is currently frozen, tell the newcomer (unfrozen is the default).
            if (hdm.GetFrozenOwnBody())
                _ = relay.SendFreezeUpdate(new FreezeUpdatePayload { SubjectId = "", SenderContentId = self, Frozen = true });
        });
    }

    // ═══════════════════════════ RECEIVER (wire → local mirror) ═══════════════════════════

    private void OnDisguiseReceived(DisguiseUpdatePayload d)
    {
        if (IsSelf(d.SenderContentId)) return;   // never mirror our own broadcast
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (string.IsNullOrEmpty(d.SubjectId))
            {
                // Own body of a remote DM → drives their SYNCED puppet body (must be bound to a local object).
                lastOwnBody[d.SenderContentId] = d;
                var idx = ResolveObjectIndex(d.SenderContentId);
                if (idx < 0) return;   // not in render range yet → applied on OnPeerBound from the cache above
                if (d.Kind == 0) hdm.RevertDisguise(idx);
                else hdm.ApplyDisguise(idx, ToAtom(d));
            }
            else
            {
                // A puppet subject. b189: DESPAWN is explicit now (was inferred from Kind==0, which also collides with
                // "spawn a blank clone" and "revert a puppet's guise" - both non-despawn). Branches:
                //   Despawn=true                    → drop our mirror.
                //   not despawn, no mirror yet      → SPAWN (first sight). A Kind-0 atom = a blank clone; HDM's
                //                                     SpawnPuppet honours Kind 0 (spawns the actor, skips the guise),
                //                                     so a summoned-but-undisguised puppet now mirrors + tracks position.
                //   not despawn, mirror exists      → Kind 0 = un-guise (keep the actor); else re-apply the disguise.
                if (d.Despawn)
                {
                    if (mirrorPuppets.TryGetValue(d.SubjectId, out var gone))
                    {
                        hdm.DespawnPuppet(gone);
                        mirrorPuppets.Remove(d.SubjectId);
                        stateApply.ForgetMirrorLocomotion(gone);   // clean EDGE if the index is later reused
                    }
                    return;
                }
                if (mirrorPuppets.TryGetValue(d.SubjectId, out var li))
                {
                    if (d.Kind == 0) hdm.RevertDisguise(li);   // puppet un-guised but still spawned - keep the mirror
                    else hdm.ApplyDisguise(li, ToAtom(d));
                }
                else
                {
                    var spawned = hdm.SpawnPuppet(ToAtom(d));   // Kind 0 → blank mirror; Kind 1/2/3 → spawned + guised
                    if (spawned >= 0)
                    {
                        mirrorPuppets[d.SubjectId] = spawned;
                        // Freeze-anim: if a freeze edge for this subject arrived before the mirror existed, apply it now.
                        if (lastFrozenPuppet.TryGetValue(d.SubjectId, out var fr) && fr)
                            hdm.SetFrozen(spawned, true);
                    }
                    else log.Debug("[HMSync] disguise-sync: mirror puppet spawn failed for " + d.SubjectId);
                }
            }
        });
    }

    private void OnActionPulseReceived(ActionPulsePayload a)
    {
        if (IsSelf(a.SenderContentId)) return;
        _ = framework.RunOnFrameworkThread(() =>
        {
            int idx;
            if (string.IsNullOrEmpty(a.SubjectId)) idx = ResolveObjectIndex(a.SenderContentId);
            else idx = mirrorPuppets.TryGetValue(a.SubjectId, out var li) ? li : -1;
            if (idx >= 0) hdm.PlayAction(idx, a.PlayId);
        });
    }

    private void OnPuppetMoveReceived(PuppetMovePayload p)
    {
        if (IsSelf(p.SenderContentId)) return;
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (mirrorPuppets.TryGetValue(p.SubjectId, out var li))
            {
                hdm.MovePuppet(li, p.X, p.Y, p.Z, p.Rot);
                stateApply.DriveMirrorLocomotion(li, p.Anim);   // animate the mirror so it walks/runs instead of sliding
            }
            // unknown subject (move before spawn) → drop; self-corrects on the next move after the DisguiseUpdate lands
        });
    }

    // Bug B: a remote DM's own-body hide bit arrived. Suppress (Alpha 0) or restore their own-body mirror. Kept in
    // StateApplyService (it owns the per-frame re-assert + the object-table resolve + unsafe Character* access). The set
    // is authoritative there, so a bit that arrives before the DM is in render range is honoured the moment they bind.
    private void OnOwnBodyHiddenReceived(OwnBodyHiddenPayload p)
    {
        if (IsSelf(p.SenderContentId)) return;
        _ = framework.RunOnFrameworkThread(() => stateApply.SetOwnBodyHidden(p.SenderContentId, p.Hidden));
    }

    // Freeze-anim: a remote DM toggled the freeze checkbox for one of their actors. Resolve the subject to a LOCAL
    // object and drive HDM's SetFrozen (HDM pins the animation speed every frame thereafter, so one call sticks). The
    // bit is cached so a freeze that arrives before the target actor exists locally is honoured on bind / mirror-spawn.
    private void OnFreezeReceived(FreezeUpdatePayload p)
    {
        if (IsSelf(p.SenderContentId)) return;
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (string.IsNullOrEmpty(p.SubjectId))
            {
                // Remote DM's own body → their SYNCED puppet body (must be bound to a local object).
                lastFrozenOwnBody[p.SenderContentId] = p.Frozen;
                var idx = ResolveObjectIndex(p.SenderContentId);
                if (idx < 0) return;   // not in render range yet → applied on OnPeerBound from the cache above
                hdm.SetFrozen(idx, p.Frozen);
            }
            else
            {
                // A puppet subject → our local mirror. Cache so a freeze that beats the DisguiseUpdate applies on spawn.
                lastFrozenPuppet[p.SubjectId] = p.Frozen;
                if (mirrorPuppets.TryGetValue(p.SubjectId, out var li))
                    hdm.SetFrozen(li, p.Frozen);
            }
        });
    }

    // ═══════════════════════════ LIFECYCLE (called by the plugin, framework thread) ═══════════════════════════

    // A peer's body just bound to a local object index → apply any cached own-body disguise that arrived early.
    public void OnPeerBound(ushort objectIndex)
    {
        var cid = ContentIdForObjectIndex(objectIndex);
        if (cid == 0) return;
        if (lastOwnBody.TryGetValue(cid, out var d) && d.Kind != 0)
            hdm.ApplyDisguise(objectIndex, ToAtom(d));
        // Freeze-anim: re-assert a cached own-body freeze the moment the peer binds (unfrozen is the default, so only true).
        if (lastFrozenOwnBody.TryGetValue(cid, out var fr) && fr)
            hdm.SetFrozen(objectIndex, true);
    }

    // A peer left the session → revert their body disguise + despawn every mirror puppet we spawned for them.
    public void OnPeerDeparted(ulong contentId)
    {
        if (contentId == 0) return;
        var idx = ResolveObjectIndex(contentId);
        if (idx >= 0)
        {
            hdm.RevertDisguise(idx);
            hdm.SetFrozen(idx, false);   // Freeze-anim: unpin the departing peer's own body so a reused index isn't stuck
        }
        lastOwnBody.Remove(contentId);
        lastFrozenOwnBody.Remove(contentId);
        stateApply.SetOwnBodyHidden(contentId, false);   // Bug B: drop any own-body suppression for the departing source

        var prefix = contentId.ToString() + ":";
        var stale = new List<string>();
        foreach (var kv in mirrorPuppets)
            if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)) stale.Add(kv.Key);
        foreach (var key in stale)
        {
            var li = mirrorPuppets[key];
            hdm.DespawnPuppet(li);
            mirrorPuppets.Remove(key);
            stateApply.ForgetMirrorLocomotion(li);
        }
        // Freeze-anim: drop any cached puppet-freeze bits for the departing source (mirrors are despawned above).
        var staleFrozen = new List<string>();
        foreach (var kv in lastFrozenPuppet)
            if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)) staleFrozen.Add(kv.Key);
        foreach (var key in staleFrozen) lastFrozenPuppet.Remove(key);
    }

    // Session teardown / disconnect → drop every mirror we own and clear caches.
    public void Reset()
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            foreach (var li in mirrorPuppets.Values) { hdm.DespawnPuppet(li); stateApply.ForgetMirrorLocomotion(li); }
            mirrorPuppets.Clear();
            lastOwnBody.Clear();
            lastFrozenOwnBody.Clear();
            lastFrozenPuppet.Clear();
            localPuppets.Clear();
        });
    }

    // ═══════════════════════════ helpers ═══════════════════════════

    private static string PuppetSubject(ulong ownerCid, int slot) => ownerCid.ToString() + ":" + slot;

    private ulong RefreshSelfCid()
    {
        var cid = localContentId();
        if (cid != 0) cachedSelfCid = cid;
        return cachedSelfCid;
    }

    private bool IsSelf(ulong senderCid) => senderCid != 0 && senderCid == cachedSelfCid;

    // Resolve a source ContentId to its bound local object index, or -1 if the peer isn't in render range yet.
    private int ResolveObjectIndex(ulong contentId)
    {
        foreach (var info in stateApply.Peers.Values)
            if (info.ContentId == contentId && info.ObjectIndex.HasValue)
                return info.ObjectIndex.Value;
        return -1;
    }

    private ulong ContentIdForObjectIndex(ushort objectIndex)
    {
        foreach (var info in stateApply.Peers.Values)
            if (info.ObjectIndex.HasValue && info.ObjectIndex.Value == objectIndex)
                return info.ContentId;
        return 0;
    }

    private static DisguiseUpdatePayload BuildDisguise(string subject, ulong self, HdmDisguiseAtom atom) => new()
    {
        SubjectId = subject,
        SenderContentId = self,
        Epoch = atom.Epoch,
        Kind = atom.Kind,
        BaseId = atom.BaseId,
        ModelCharaId = atom.ModelCharaId,
        Scale = atom.Scale,
        VOffset = atom.VOffset,
        LoopId = atom.LoopId,
    };

    private static HdmDisguiseAtom ToAtom(DisguiseUpdatePayload d) => new()
    {
        Epoch = d.Epoch,
        Kind = d.Kind,
        BaseId = d.BaseId,
        ModelCharaId = d.ModelCharaId,
        Scale = d.Scale,
        VOffset = d.VOffset,
        LoopId = d.LoopId,
    };

    public void Dispose()
    {
        try { hdm.OnDisguiseChanged -= OnLocalDisguiseChanged; } catch { }
        try { hdm.OnActionFired -= OnLocalActionFired; } catch { }
        try { hdm.OnPuppetSpawned -= OnLocalPuppetSpawned; } catch { }
        try { hdm.OnPuppetDespawned -= OnLocalPuppetDespawned; } catch { }
        try { hdm.OnPuppetMoved -= OnLocalPuppetMoved; } catch { }
        try { hdm.OnOwnBodyHidden -= OnLocalOwnBodyHidden; } catch { }
        try { hdm.OnFreezeChanged -= OnLocalFrozen; } catch { }
        try { hdm.OnHdmReady -= BroadcastFullState; } catch { }
        try { relay.OnDisguiseReceived -= OnDisguiseReceived; } catch { }
        try { relay.OnActionPulseReceived -= OnActionPulseReceived; } catch { }
        try { relay.OnPuppetMoveReceived -= OnPuppetMoveReceived; } catch { }
        try { relay.OnOwnBodyHiddenReceived -= OnOwnBodyHiddenReceived; } catch { }
        try { relay.OnFreezeReceived -= OnFreezeReceived; } catch { }
    }
}
