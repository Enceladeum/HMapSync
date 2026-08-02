using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HMSync.Services;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
// NB-19 Phase 1 — Quest-gated NPC populace READ-SPOOF. Design & offsets from Fable (7.55 disassembly, exe build
// 2026.07.16.0001.0000). This is the payoff of the Phase-0 probe (the former QuestProbeService): the b12 in-game
// verdict CONFIRMED the 1160 populace path reads local quest state through IsQuestComplete — during a cold virtual
// 1160 load it queried IsQuestComplete(4738) and IsQuestComplete(4739) (both -> 0), and NOTHING else in the chain,
// no GetQuestSequence, no IsQuestAccepted. That is a textbook post-quest gate: force those chain completions to read
// TRUE and the end-quest public cast (QST_MAIN_PUB layer, ~16 NPCs) becomes visible.
//
// TWO-GATE MODEL recap: Gate 1 = layer-filter key (NB-18) decides which layout instances EXIST (geometry + which
// EventNpc instances are present) — quest-state-free. Gate 2 (THIS) = a quest-driven populace pass that reads LOCAL
// QuestManager state to decide which of the already-present EventNpc instances become VISIBLE. All quest reads funnel
// through three QuestManager member functions; every CS static wrapper / "complete OR seq>=N" variant is a thin shim
// over these, so hooking the funnels is coherent by construction.
//
// PROPAGATION — the blanket "Solo Instances" end-state (2026-08-02, user-directed). The b13 in-game result on 1160
// confirmed the end-state populace renders correctly. Rather than probe every solo-instance zone one at a time, the
// spoof now generalises to the whole category: any virtual load of a zone whose TerritoryIntendedUse is 15 or 54
// ("Solo Instances", the same classifier the map picker uses — HMSyncUI CategoryFor) arms a BLANKET end-state — every
// IsQuestComplete read returns TRUE, presenting the fully-progressed world. This is the ADDITIVE default; the
// subtractive layer is NpcVisibilityService (host-synced DespawnNpcs / HideQuestSigns), and finer per-NPC removal is
// planned on top. "Show the finished world, then remove what you don't want." Only IsQuestComplete is blanketed;
// GetQuestSequence / IsQuestAccepted stay pass-through — a completed quest has no active sequence, so end-state
// semantics are exactly "complete=true, sequence=0", which is what leaving those two untouched yields (1160 confirmed
// the populace evaluator reads only IsQuestComplete anyway). An explicit per-zone override table still exists and
// takes PRECEDENCE over the blanket, for the rare zone that needs a specific completed/active set rather than all-true.
//
// SPOOF DISCIPLINE (safety-critical):
//   • Hooks return SPOOFED VALUES ONLY. QuestManager memory is NEVER written — above all not the CompletedQuests
//     bitmap @0x2E0 (persistent, server-synced character state). A returned value reaches nothing on the server.
//   • A spoof is active ONLY while armed, and it is armed ONLY from inside HMS's own loader (LoadZone → ArmForZone),
//     disarmed on Revert. Real game zone changes never run LoadZone → never armed, so a real visit to any solo
//     instance reads its true, unspoofed quest state. The blanket rule is a deterministic function of the zone's
//     IntendedUse, so peers converge with NO broadcast (same convergence property as NB-18-SHIP's forced cast key).
//   • The blanket is a deliberate uniform "present the end-state" policy, NOT a guessed per-quest value — it is
//     read-only and virtual-load-gated, so its blast radius is cosmetic (which NPCs show) and reversible via the
//     removal toggle. Explicit per-zone overrides, by contrast, keep the measured-not-guessed rule: an override's
//     specific ids belong there only after a /hms questprobe pass has MEASURED them.
//
// The /hms questprobe command survives as a diagnostic: it toggles logReads (log each distinct (fn,quest,result),
// annotating whether we SPOOFED it), independent of whether a spoof is armed. Hooks are enabled whenever a spoof is
// armed (blanket or explicit) OR logReads is on, and disabled otherwise, so the very hot funnels (IsQuestComplete
// alone has 178 callers) carry ZERO overhead in the common no-virtual-load case.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
internal sealed unsafe class QuestSpoofService : IDisposable
{
    private readonly ISigScanner sig;
    private readonly IGameInteropProvider hooks;
    private readonly IFramework framework;
    private readonly IDataManager data;
    private readonly IPluginLog log;

    // "Solo Instances" TerritoryIntendedUse values — the category that gets the blanket end-state (mirrors HMSyncUI's
    // CategoryFor: 15/54 → "Solo Instances"). Single-player story/quest instances (e.g. m5e7/1160 Terncliff finale).
    private static readonly HashSet<uint> SoloInstanceUses = new() { 15, 54 };

    // 7.55 function-prologue sigs (Fable, verified count=1 each). ScanText resolves to the function start.
    private const string SigIsQuestComplete = "0F B7 C2 4C 8B C9 44 8B C0 49 C1 E8 03 49 81 F8 EF 02";   // bool(this, ushort) @0xdf5680
    private const string SigGetQuestSequence = "40 53 48 83 EC 20 0F B7 D9 E8 ?? ?? ?? ?? 45 33";          // byte(ushort)      @0xdf59d0
    private const string SigIsQuestAccepted = "45 33 C0 48 8D 41 18 66 39 10 74 10 49 FF C0 48";           // bool(this, ushort) @0xdf5750

    // Native bool returns in AL (1 byte); declaring the return as byte reads it correctly and dodges the 4-byte
    // Win32-BOOL marshalling pitfall. Member fns take this in RCX, questId in RDX (standard x64) → default marshalling.
    private delegate byte IsQuestCompleteDelegate(nint questMgr, ushort questId);
    private delegate byte GetQuestSequenceDelegate(ushort questId);
    private delegate byte IsQuestAcceptedDelegate(nint questMgr, ushort questId);

    private Hook<IsQuestCompleteDelegate>? completeHook;
    private Hook<GetQuestSequenceDelegate>? sequenceHook;
    private Hook<IsQuestAcceptedDelegate>? acceptedHook;

    // A per-zone populace policy: the quest state to present to the LOCAL populace evaluator during a virtual load.
    //   Completed    — ids IsQuestComplete must report TRUE (the "already finished this arc" set).
    //   ActiveQuest  — the one in-progress quest to present (0 = none, i.e. a pure post-quest view like 1160).
    //   ActiveSeq    — the sequence value GetQuestSequence should report for ActiveQuest.
    // Only IsQuestComplete is spoofed for the Completed set; GetQuestSequence/IsQuestAccepted spoof ONLY ActiveQuest.
    // CS0649 suppressed: the override table (Policies) ships EMPTY — the blanket covers the solo-instance category, so
    // these fields have no assignment yet. They ARE read in the detours; a future measured override assigns them.
#pragma warning disable CS0649
    private sealed class QuestPolicy
    {
        public required HashSet<ushort> Completed;
        public ushort ActiveQuest;   // 0 = no active quest
        public byte ActiveSeq;       // GetQuestSequence value for ActiveQuest
    }
#pragma warning restore CS0649

    // EXPLICIT per-zone overrides — take PRECEDENCE over the blanket end-state, for the rare zone that needs a specific
    // completed/active set rather than all-true (e.g. a mid-quest stage whose richest populace is DURING a quest, or a
    // NON-solo-instance zone with a single quest-gated NPC). MEASURED ids only (a /hms questprobe pass), same discipline
    // as ForcedCastKeys / the chat whitelist. Empty by default — 1160 needs no entry: it is a Solo Instance (use 15), so
    // the blanket already presents its end-state (the measured gate 4738/4739 is a strict subset of "all complete").
    // Reference (measured, b12): 1160/m5e7 populace reads only IsQuestComplete(4738) & (4739); full chain is 4735-4743.
    private static readonly Dictionary<uint, QuestPolicy> Policies = new();

    private volatile QuestPolicy? policy;   // armed explicit override (null = none). Read inside detours; set on framework thread.
    private volatile bool blanket;          // armed blanket end-state (Solo Instance): IsQuestComplete → always true.
    private volatile bool logReads;         // /hms questprobe diagnostic: log each distinct (fn,quest,result) once.

    private readonly HashSet<long> seen = new();   // dedup key for logReads: (fnCode<<32)|(questId<<8)|result
    private int distinctLogged;
    private long logCloseTick;                      // Environment.TickCount64 auto-stop deadline for logReads
    private const int LogWindowMs = 120_000;        // ~2 min so a forgotten /hms questprobe can't log forever
    private const int MaxDistinct = 4000;           // hard cap so a runaway can never flood /xllog
    private static readonly string[] FnNames = { "IsQuestComplete", "GetQuestSequence", "IsQuestAccepted" };

    public bool LogReadsActive => logReads;

    public QuestSpoofService(ISigScanner sig, IGameInteropProvider hooks, IFramework framework, IDataManager data, IPluginLog log)
    {
        this.sig = sig;
        this.hooks = hooks;
        this.framework = framework;
        this.data = data;
        this.log = log;
    }

    // Create (but do NOT enable) the three hooks. A failed resolve is a logged no-op, never a crash — that funnel
    // simply can't be spoofed/logged. framework.Update drives the logReads auto-stop only (cheap no-op otherwise).
    public void Initialize()
    {
        completeHook = TryHook<IsQuestCompleteDelegate>(SigIsQuestComplete, IsQuestCompleteDetour, "IsQuestComplete");
        sequenceHook = TryHook<GetQuestSequenceDelegate>(SigGetQuestSequence, GetQuestSequenceDetour, "GetQuestSequence");
        acceptedHook = TryHook<IsQuestAcceptedDelegate>(SigIsQuestAccepted, IsQuestAcceptedDetour, "IsQuestAccepted");
        framework.Update += OnUpdate;
    }

    private Hook<T>? TryHook<T>(string signature, T detour, string name) where T : Delegate
    {
        try
        {
            var addr = sig.ScanText(signature);
            var h = hooks.HookFromAddress<T>(addr, detour);
            log.Information("[HMSync] [QUESTSPOOF] " + name + " resolved @0x" + addr.ToString("X"));
            return h;
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [QUESTSPOOF] " + name + " hook resolve failed: " + ex.Message);
            return null;
        }
    }

    // ── Policy lifecycle (called from ZoneLoadService on the framework thread) ──────────────────────────────────

    // Arm the populace spoof for a zone. Precedence: (1) an explicit per-zone override wins; (2) else a Solo Instance
    // (IntendedUse 15/54) gets the blanket end-state; (3) else no spoof. Always clears the other mode first so nothing
    // stale from a prior load leaks in.
    public void ArmForZone(uint territoryId)
    {
        if (Policies.TryGetValue(territoryId, out var p))
        {
            blanket = false;
            policy = p;
            SyncHookState();
            log.Information("[HMSync] [QUESTSPOOF] armed EXPLICIT override for zone " + territoryId + " (completed=" +
                p.Completed.Count + " ids, active=" + p.ActiveQuest + "). Spoofed until Revert.");
            return;
        }

        uint use = ResolveIntendedUse(territoryId);
        if (SoloInstanceUses.Contains(use))
        {
            policy = null;
            blanket = true;
            SyncHookState();
            log.Information("[HMSync] [QUESTSPOOF] armed BLANKET end-state for Solo Instance zone " + territoryId +
                " (IntendedUse " + use + "): IsQuestComplete → true. Spoofed until Revert.");
            return;
        }

        Disarm();   // not an override, not a solo instance → no spoof (and clear any stale arm)
    }

    // Drop the populace spoof (called on Revert, before the real origin zone reloads).
    public void Disarm()
    {
        if (policy == null && !blanket) return;
        policy = null;
        blanket = false;
        SyncHookState();
        log.Information("[HMSync] [QUESTSPOOF] disarmed.");
    }

    // TerritoryType → TerritoryIntendedUse.RowId (0 on any miss). Same lookup the chat-rule restore uses.
    private uint ResolveIntendedUse(uint territoryId)
    {
        try
        {
            var row = data.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryId);
            return row != null ? row.Value.TerritoryIntendedUse.RowId : 0;
        }
        catch { return 0; }
    }

    // ── Diagnostic (/hms questprobe) ───────────────────────────────────────────────────────────────────────────

    public bool ToggleLogReads()
    {
        if (logReads)
        {
            logReads = false;
            SyncHookState();
            log.Information("[HMSync] [QUESTSPOOF] read-log OFF. " + distinctLogged + " distinct (fn,quest,result) tuples this window.");
        }
        else
        {
            seen.Clear();
            distinctLogged = 0;
            logReads = true;
            logCloseTick = Environment.TickCount64 + LogWindowMs;
            SyncHookState();
            log.Information("[HMSync] [QUESTSPOOF] read-log ON (~" + (LogWindowMs / 1000) + "s). Logging IsQuestComplete / " +
                "GetQuestSequence / IsQuestAccepted; SPOOFED reads are annotated. Chain targets 4735-4743.");
        }
        return logReads;
    }

    // Hooks live iff a policy is armed OR read-logging is on; otherwise they carry zero overhead. Enable/Disable are
    // idempotent, so recomputing the desired state on every change is safe. Called only on the framework thread.
    private void SyncHookState()
    {
        bool want = policy != null || blanket || logReads;
        if (want)
        {
            completeHook?.Enable();
            sequenceHook?.Enable();
            acceptedHook?.Enable();
        }
        else
        {
            completeHook?.Disable();
            sequenceHook?.Disable();
            acceptedHook?.Disable();
        }
    }

    // Auto-close the read-log window on the framework thread (same thread the detours run on, so Disable can't race a
    // live detour). Never touches the spoof policy — a policy is only dropped by Revert.
    private void OnUpdate(IFramework fw)
    {
        if (logReads && Environment.TickCount64 >= logCloseTick)
        {
            logReads = false;
            SyncHookState();
            log.Information("[HMSync] [QUESTSPOOF] read-log auto-closed (timeout). " + distinctLogged + " distinct tuples.");
        }
    }

    // ── Detours ────────────────────────────────────────────────────────────────────────────────────────────────
    // Each: if an armed policy dictates a value, RETURN the spoof (Original is not called — the game never sees the
    // real read, but nothing is written either); otherwise call Original and pass its result through. logReads only
    // observes; it never changes what is returned.

    private byte IsQuestCompleteDetour(nint mgr, ushort questId)
    {
        if (blanket) { if (logReads) LogRead(0, questId, 1, true); return 1; }   // Solo Instance end-state: everything complete
        var p = policy;
        if (p != null)
        {
            if (p.Completed.Contains(questId)) { if (logReads) LogRead(0, questId, 1, true); return 1; }
            if (p.ActiveQuest != 0 && questId == p.ActiveQuest) { if (logReads) LogRead(0, questId, 0, true); return 0; }
        }
        byte r = completeHook!.Original(mgr, questId);
        if (logReads) LogRead(0, questId, r, false);
        return r;
    }

    private byte GetQuestSequenceDetour(ushort questId)
    {
        var p = policy;
        if (p != null && p.ActiveQuest != 0 && questId == p.ActiveQuest)
        {
            if (logReads) LogRead(1, questId, p.ActiveSeq, true);
            return p.ActiveSeq;
        }
        byte r = sequenceHook!.Original(questId);
        if (logReads) LogRead(1, questId, r, false);
        return r;
    }

    private byte IsQuestAcceptedDetour(nint mgr, ushort questId)
    {
        var p = policy;
        if (p != null && p.ActiveQuest != 0 && questId == p.ActiveQuest)
        {
            if (logReads) LogRead(2, questId, 1, true);
            return 1;
        }
        byte r = acceptedHook!.Original(mgr, questId);
        if (logReads) LogRead(2, questId, r, false);
        return r;
    }

    // Log each distinct (fn,quest,result) once, flagging chain targets and whether we spoofed it. try/catch so a
    // logging fault can NEVER propagate into native code.
    private void LogRead(int fnCode, ushort questId, byte result, bool spoofed)
    {
        try
        {
            long key = ((long)fnCode << 32) | ((long)questId << 8) | result;
            if (!seen.Add(key)) return;                 // already logged this tuple
            if (distinctLogged >= MaxDistinct) return;  // runaway guard
            distinctLogged++;
            bool target = questId >= 4735 && questId <= 4743;
            log.Information("[HMSync] [QUESTSPOOF] " + FnNames[fnCode] + "(" + questId + ") -> " + result +
                (spoofed ? " (SPOOFED)" : "") + (target ? "   <== 1160 CHAIN TARGET" : "") + "   #" + distinctLogged);
        }
        catch { /* never throw into the native caller */ }
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        try { completeHook?.Dispose(); } catch { }
        try { sequenceHook?.Dispose(); } catch { }
        try { acceptedHook?.Dispose(); } catch { }
    }
}
