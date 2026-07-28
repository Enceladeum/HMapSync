using System;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace HMSync.Services;

// COSM_1_016 - SKILLS: cosmetic action replay (v0.7.365).
//
// WHAT THIS IS. In session, the packet firewall drops every outbound opcode except the heartbeat, so a skill you press
// never reaches the server - which means PEERS never hear about it. Your OWN client still presents the cast normally
// (animation, VFX, sound all play locally without server confirmation), so nothing needs fixing locally. This service
// exists purely to carry "I cast X" to peers and replay it on their puppet of you.
//
// CAPTURE. Hook ActionManager.UseAction and let the ORIGINAL RUN FIRST, then read its return. Passing through (rather
// than suppressing) is deliberate: the game then enforces cooldowns, GCD, range, "you cannot use that here", casting
// state etc. exactly as normal, so behaviour stays faithful instead of us reimplementing the restriction system. We
// only record a cast the client itself ACCEPTED (ret == true).
//
// REPLAY. ActionEffectHandler.Receive is the engine's own entry point for "a remote character performed an action" -
// the function the game runs on incoming ActionEffect packets. Driving it gives the whole presentation cascade for
// free (caster animation, VFX, sound, telegraphs) instead of hand-composing pieces. We pass NumTargets = 0, so the
// action presents but applies NO effect to anyone: cosmetic by construction, not by filtering.
//
// HDM. ReplayOn(Character*, ...) takes any character, so the future director module calls the same primitive to make a
// spawned NPC cast. That reuse is why this was built before it was strictly needed for RP.
public sealed unsafe class SkillSyncService : IDisposable
{
    private delegate bool UseActionDelegate(ActionManager* mgr, ActionType actionType, uint actionId,
        ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted);

    private readonly IPluginLog log;
    private readonly IGameInteropProvider hooks;
    private readonly Func<bool> isSessionActive;

    private Hook<UseActionDelegate>? useActionHook;

    // Last locally-accepted cast, picked up by the sender on its next WARM projection.
    public uint PendingActionId { get; private set; }
    public byte PendingActionType { get; private set; }
    public uint PendingActionEpoch { get; private set; }
    public Vector3 PendingActionTarget { get; private set; }
    // v0.7.367: the TARGET's stable ContentId. A raw targetId is a client-local entity id and is meaningless on
    // another machine, so the first build defaulted the replay's animation target to the caster - which made every
    // targeted action (heals especially) visibly self-cast on the receiver. ContentId is the identity HMS already
    // binds peers on (stable across worlds/zones), so it translates correctly on the far side. 0 = no/unresolvable
    // target → the receiver falls back to the caster, which is right for self-casts and ground AoE.
    public ulong PendingActionTargetCid { get; private set; }

    public SkillSyncService(IPluginLog log, IGameInteropProvider hooks, Func<bool> isSessionActive)
    {
        this.log = log;
        this.hooks = hooks;
        this.isSessionActive = isSessionActive;
    }

    public void Init()
    {
        try
        {
            // Sig from ClientStructs' [MemberFunction] on ActionManager.UseAction. HookFromSignature (never a manual
            // rel32 resolve - that crashes in HookManager.FollowJmp).
            useActionHook = hooks.HookFromSignature<UseActionDelegate>("E8 ?? ?? ?? ?? B0 01 EB B6", UseActionDetour);
            useActionHook.Enable();
            log.Information("[HMSync] [SKILL] UseAction hook installed.");
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [SKILL] UseAction hook failed: " + ex.Message);
        }
    }

    private bool UseActionDetour(ActionManager* mgr, ActionType actionType, uint actionId,
        ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        // Original FIRST - the game applies all its own gating, and we only care about casts it accepted.
        bool ret = useActionHook!.Original(mgr, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
        try
        {
            if (ret && isSessionActive())
            {
                PendingActionId = actionId;
                PendingActionType = (byte)actionType;
                PendingActionEpoch++;               // monotonic - the receiver replays only on CHANGE
                PendingActionTarget = ReadTargetPos(targetId);
                PendingActionTargetCid = ReadTargetContentId(targetId);
                log.Debug("[HMSync] [SKILL] captured action " + actionId + " (type " + actionType + ") epoch " + PendingActionEpoch
                    + " tgtCid=" + PendingActionTargetCid + ".");
            }
        }
        catch (Exception ex) { log.Debug("[HMSync] [SKILL] capture failed: " + ex.Message); }
        return ret;
    }

    // Ground-targeted actions land at a position; for everything else this is unused by the replay (the engine uses
    // the caster/target objects). Best-effort: resolve the target object's position when we can.
    private Vector3 ReadTargetPos(ulong targetId)
    {
        try
        {
            var tgt = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager.Instance()
                ->Objects.GetObjectByGameObjectId(targetId);
            if (tgt != null) return tgt->Position;
        }
        catch { }
        return Vector3.Zero;
    }

    // Resolve the target's STABLE identity (ContentId) so the receiver can find its own copy of that character.
    // Non-player targets (NPCs, ground) have no ContentId → 0 → receiver animates on the caster.
    private ulong ReadTargetContentId(ulong targetId)
    {
        try
        {
            var tgt = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager.Instance()
                ->Objects.GetObjectByGameObjectId(targetId);
            if (tgt == null) return 0;
            if (tgt->ObjectKind != FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.Pc) return 0;
            return ((Character*)tgt)->ContentId;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Replay a cast on ANY character - a peer puppet today, an HDM-spawned NPC tomorrow. Presentation only:
    /// NumTargets = 0 means the engine plays animation/VFX/sound and applies no effect to anybody.
    /// </summary>
    public void ReplayOn(Character* caster, uint actionId, byte actionType, Vector3 targetPos, Character* target = null)
    {
        if (caster == null || actionId == 0) return;
        try
        {
            uint entityId = caster->GameObject.EntityId;

            // The action's animation plays on AnimationTargetId. Pointing it at the caster made every targeted action
            // (heals, single-target buffs) self-cast on the receiver even though it looked correct on the caster's own
            // client. Use the resolved target when we have one; fall back to the caster for self-casts, ground AoE and
            // unresolvable targets - which is the correct behaviour for those.
            var animTarget = target != null ? target : caster;

            var header = default(ActionEffectHandler.Header);
            header.AnimationTargetId = animTarget->GameObject.GetGameObjectId();
            header.ActionId = actionId;
            header.ActionType = actionType;
            header.SpellId = (ushort)ActionManager.GetSpellIdForAction((ActionType)actionType, actionId);
            header.GlobalSequence = 0;      // client-synthesised; not a server sequence
            header.SourceSequence = 0;      // 0 = "not client-initiated" → no animation lock forced on the puppet
            header.AnimationLock = 0f;
            header.NumTargets = 0;          // ← cosmetic by construction: presentation without effects
            header.ShowInLog = false;       // never write to the action log - this isn't a real combat event
            header.ForceAnimationLock = false;
            header.RotationInt = (ushort)0;
            header.AnimationVariation = 0;

            var pos = targetPos;
            ActionEffectHandler.Receive(entityId, caster, &pos, &header, null, null);
        }
        catch (Exception ex)
        {
            log.Debug("[HMSync] [SKILL] replay failed for action " + actionId + ": " + ex.Message);
        }
    }

    public void Dispose()
    {
        try { useActionHook?.Disable(); useActionHook?.Dispose(); } catch { }
    }
}
