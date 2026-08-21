using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace HMSync.Services;

// NB-40 - EMOTES: let NATIVE emote triggers fire LOCKED emotes while in a HMS session, so `/wave`, the emote
// menu, and hotbar macros behave like `/hms emote <id>` already does - and like UNLOCKED native emotes already
// do (fire locally + sync to peers). Closes the "unlocked work, locked don't" asymmetry the user flagged.
//
// TWO LAYERS, learned the hard way (b84→b87). Opening the native emote path needs BOTH:
//
//   LAYER 1 - the GUARDS (open the pre-checks so the command reaches its executor at all):
//     1. UIState.IsEmoteUnlocked(id)        - "do you own it"      → refuses "you have not unlocked this emote"
//     2. EmoteManager.CanExecuteEmote(id)   - "can you do it now"  → refuses "<emote> can't be used right now"
//   Opening ONLY these (b84/b85/b86) got the native command PAST the visible refusals - but it then died
//   SILENTLY, because the game's EXECUTOR itself refuses an unowned emote internally, past both guards.
//
//   LAYER 2 - the EXECUTOR REDIRECT (b87): hook the two executors the native path can reach -
//     AgentEmote.ExecuteEmote (the emote-window/agent path) and EmoteManager.ExecuteEmote (the lower path) -
//     and for a GENUINELY-LOCKED emote in-session, DON'T call the game's (refusing) Original; instead invoke
//     HMS's own force-play (forcePlayLocked → PlayResolvedEmote), which drives the ActionTimeline directly - the
//     EXACT path `hms emote <locked>` already uses. The native trigger now rides the proven client-force path,
//     and LocalStateDetector (trigger-agnostic) syncs it to peers. Redirecting at BOTH executors covers the
//     path regardless of whether the text command routes through the agent or straight to the manager; whichever
//     fires first suppresses the other (agent redirect returns before reaching the manager), so no double-play.
//
// SelfCall. HMS sets EmoteUnlockService.SelfCall around its own PlayResolvedEmote; while set ALL four detours
// return Original (the truth) and skip the redirect. So `hms emote <id>` judges on genuine ownership exactly as
// prod does (unchanged, still force-plays locked emotes client-side), and neither the guard spoof nor the
// executor redirect leaks into HMS's own play. The force-play HMS runs from inside the redirect also sets
// SelfCall, so it is not re-entrantly intercepted.
//
// SURGICAL, NOT BLANKET. Gate 2 legitimately refuses UNLOCKED emotes for genuine STATE reasons - e.g. Gridanian
// Gulp (standing-only) refuses while seated, which is correct and must be preserved (the seated-Gulp bug). So
// both the CanExecuteEmote guard AND the executor redirect act ONLY when the emote is GENUINELY locked (its real,
// un-spoofed ownership is false). A genuinely-unlocked emote keeps the engine's real answer and the engine's own
// executor, so native unlocked emotes fire exactly as before and state refusals still hold. The distinguishing
// signal is the real unlock bit, read through gate-1's Original (GenuinelyUnlocked).
//
// WHY SAFE. The guard predicates are pure READS; the executor redirect only DIVERTS a locked-in-session play to
// HMS's proven client-force path and suppresses the engine's own (which was going to refuse anyway). Nothing is
// persisted and everything reverts truthfully out of session. The cast never reaches the server: in session the
// packet firewall already drops outbound emote opcodes (the exact reason unlocked native emotes fire+sync
// cleanly today); locked ones now ride the identical proven path, synced by the trigger-agnostic LocalStateDetector.
public sealed unsafe class EmoteUnlockService : IDisposable
{
    private delegate bool IsEmoteUnlockedDelegate(UIState* thisPtr, ushort emoteId);
    private delegate bool CanExecuteEmoteDelegate(EmoteManager* thisPtr, ushort emoteId);
    // Executor delegates - pointers as nint (size-correct; the exact PlayEmoteOption* type is irrelevant to the
    // hook and avoids dragging its namespace in). AgentEmote.ExecuteEmote returns void; EmoteManager.ExecuteEmote
    // returns bool (success).
    private delegate void AgentExecuteEmoteDelegate(nint thisPtr, ushort emoteId, nint opt, bool addToHistory, bool liveUpdate);
    private delegate bool MgrExecuteEmoteDelegate(nint thisPtr, ushort emoteId, nint opt);

    private readonly IPluginLog log;
    private readonly IGameInteropProvider hooks;
    private readonly Func<bool> isSessionActive;
    // HMS force-play for a locked emote id (resolve timelines → PlayResolvedEmote). Returns true when handled.
    private readonly Func<ushort, bool> forcePlayLocked;

    private Hook<IsEmoteUnlockedDelegate>? unlockedHook;
    private Hook<CanExecuteEmoteDelegate>? canExecHook;
    private Hook<AgentExecuteEmoteDelegate>? agentExecHook;
    private Hook<MgrExecuteEmoteDelegate>? mgrExecHook;

    // Set by HMS around its OWN emote play (PlayResolvedEmote). While true, every detour passes through to
    // Original and skips the redirect - the spoof/redirect is only ever meant for the game's native emote path.
    // Framework-thread only; no locking needed.
    public bool SelfCall;

    public EmoteUnlockService(IPluginLog log, IGameInteropProvider hooks, Func<bool> isSessionActive,
        Func<ushort, bool> forcePlayLocked)
    {
        this.log = log;
        this.hooks = hooks;
        this.isSessionActive = isSessionActive;
        this.forcePlayLocked = forcePlayLocked;
    }

    public void Init()
    {
        // Sigs from ClientStructs' [MemberFunction]s (E8-leading call-site sigs; Dalamud's ScanText follows the
        // rel32 to the function). HookFromSignature only - never a manual resolve.
        try
        {
            unlockedHook = hooks.HookFromSignature<IsEmoteUnlockedDelegate>(
                "E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? ?? ?? 41 B8", UnlockedDetour);
            unlockedHook.Enable();
            log.Information("[HMSync] [EMOTE-UNLOCK] IsEmoteUnlocked hook installed.");
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [EMOTE-UNLOCK] IsEmoteUnlocked hook failed: " + ex.Message);
        }

        try
        {
            canExecHook = hooks.HookFromSignature<CanExecuteEmoteDelegate>(
                "E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 48 85 F6 74 05", CanExecDetour);
            canExecHook.Enable();
            log.Information("[HMSync] [EMOTE-UNLOCK] CanExecuteEmote hook installed.");
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [EMOTE-UNLOCK] CanExecuteEmote hook failed: " + ex.Message);
        }

        try
        {
            agentExecHook = hooks.HookFromSignature<AgentExecuteEmoteDelegate>(
                "E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8B CE E8 ?? ?? ?? ?? 48 8B F8", AgentExecDetour);
            agentExecHook.Enable();
            log.Information("[HMSync] [EMOTE-UNLOCK] AgentEmote.ExecuteEmote hook installed.");
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [EMOTE-UNLOCK] AgentEmote.ExecuteEmote hook failed: " + ex.Message);
        }

        try
        {
            mgrExecHook = hooks.HookFromSignature<MgrExecuteEmoteDelegate>(
                "E8 ?? ?? ?? ?? 84 C0 0F 85 ?? ?? ?? ?? 0F B6 1D", MgrExecDetour);
            mgrExecHook.Enable();
            log.Information("[HMSync] [EMOTE-UNLOCK] EmoteManager.ExecuteEmote hook installed.");
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [EMOTE-UNLOCK] EmoteManager.ExecuteEmote hook failed: " + ex.Message);
        }
    }

    // GATE 1. In session, report every emote as owned so native triggers get past the ownership refusal. Out of
    // session - or while HMS is running its OWN emote play (SelfCall) - the truth.
    private bool UnlockedDetour(UIState* thisPtr, ushort emoteId)
    {
        try { if (isSessionActive() && !SelfCall) return true; }
        catch { }
        return unlockedHook!.Original(thisPtr, emoteId);
    }

    // GATE 2. In session, open the "can't be used right now" refusal ONLY for genuinely-locked emotes - a
    // genuinely-unlocked emote keeps the engine's real answer, so real state refusals (standing-only emote while
    // seated) still hold. Inert while HMS runs its own play (SelfCall).
    private bool CanExecDetour(EmoteManager* thisPtr, ushort emoteId)
    {
        try
        {
            if (isSessionActive() && !SelfCall && !GenuinelyUnlocked(emoteId))
                return true;
        }
        catch { }
        return canExecHook!.Original(thisPtr, emoteId);
    }

    // EXECUTOR REDIRECT (agent path). Past the two guards, the game's own executor still refuses an unowned
    // emote internally (silent no-op). For a genuinely-locked emote in-session, divert to HMS's force-play and
    // DON'T run Original - so the native trigger fires the emote through HMS's proven client-force path. Inert
    // for genuinely-unlocked emotes (they use the engine's own executor) and while HMS plays its own (SelfCall).
    private void AgentExecDetour(nint thisPtr, ushort emoteId, nint opt, bool addToHistory, bool liveUpdate)
    {
        try
        {
            if (isSessionActive() && !SelfCall && !GenuinelyUnlocked(emoteId))
            {
                log.Debug("[HMSync] [EMOTE-REDIR] agent.ExecuteEmote id=" + emoteId + " locked in-session → force-play");
                if (forcePlayLocked != null && forcePlayLocked(emoteId))
                    return;   // handled by HMS; suppress the engine's refusing executor
            }
        }
        catch (Exception ex) { log.Error("[HMSync] [EMOTE-REDIR] agent detour: " + ex.Message); }
        agentExecHook!.Original(thisPtr, emoteId, opt, addToHistory, liveUpdate);
    }

    // EXECUTOR REDIRECT (manager path). Same as the agent redirect, for the lower-level executor the text
    // command may reach directly. Returns true (success) after force-playing so any caller sees the emote as
    // executed. If the agent redirect already fired for this trigger it returned before reaching here, so this
    // only runs when the manager is the actual entry - no double-play.
    private bool MgrExecDetour(nint thisPtr, ushort emoteId, nint opt)
    {
        try
        {
            if (isSessionActive() && !SelfCall && !GenuinelyUnlocked(emoteId))
            {
                log.Debug("[HMSync] [EMOTE-REDIR] mgr.ExecuteEmote id=" + emoteId + " locked in-session → force-play");
                if (forcePlayLocked != null && forcePlayLocked(emoteId))
                    return true;   // handled by HMS; suppress the engine's refusing executor
            }
        }
        catch (Exception ex) { log.Error("[HMSync] [EMOTE-REDIR] mgr detour: " + ex.Message); }
        return mgrExecHook!.Original(thisPtr, emoteId, opt);
    }

    // The REAL, un-spoofed unlock state of an emote - reads gate-1's Original, bypassing our own hook. Used by the
    // gate-2 detour and both executor redirects to tell a lock-refusal (bypass/divert) apart from a genuine
    // state-refusal (honour).
    private bool GenuinelyUnlocked(ushort emoteId)
    {
        var uiState = UIState.Instance();
        if (uiState == null) return false;
        if (unlockedHook == null) return uiState->IsEmoteUnlocked(emoteId);   // hook absent → call is the truth
        return unlockedHook.Original(uiState, emoteId);
    }

    public void Dispose()
    {
        try { unlockedHook?.Disable(); unlockedHook?.Dispose(); } catch { }
        try { canExecHook?.Disable(); canExecHook?.Dispose(); } catch { }
        try { agentExecHook?.Disable(); agentExecHook?.Dispose(); } catch { }
        try { mgrExecHook?.Disable(); mgrExecHook?.Dispose(); } catch { }
    }
}
