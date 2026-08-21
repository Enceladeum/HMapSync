using System;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

namespace HMSync.Services;

// b120 Tier-2: a baked avfx doodad = its EnvState word offset (0x2C0/0x2C8) + the inline `.avfx` path + the raw
// descriptor bytes (path + transform block). This is the shippable identity+placement of a doodad: persist it in the
// preset, and at apply re-establish a LIVE native descriptor from Bytes and re-point the EnvState word at it (candidate
// A) — so a persisted/synced preset spawns doodads without depending on a same-session live donor pointer.
public sealed class DoodadBake
{
    public int Offset;                       // EnvState offset whose pointer word points at this descriptor
    public string Path = "";                 // inline `.avfx` resource path (census identity)
    public byte[] Bytes = Array.Empty<byte>();   // raw descriptor snapshot (path + transform)
}

// WEATHER-CRAM b96: render a weather the zone does NOT natively carry, by restamping a captured EnvState block AFTER
// the game's per-frame recompute runs.
//
// WHY A HOOK (proven by b94/b95): EnvState @EnvMgr+0x58 (0x2F8 bytes) IS what the render samples — a captured donor
// block written there shows the foreign sky. But a write from Dalamud's Framework.Update is clobbered every single
// frame: the game's UpdateEnvironment recompute runs AFTER Framework.Update, regenerates EnvState from the active
// weather, and wipes our block before the render reads it (b95 log: our write held within the frame — post==captured —
// yet `pre` reverted to the destination weather every tick). Pure ordering loss, identical to what TimeFreezeService
// hit with the Eorzea-time recompute.
//
// THE FIX (Brio's EnvironmentService idiom, AGPL — technique reused, credited): hook UpdateEnvironment, let Original
// run (so the zone's own env is computed normally), THEN overwrite EnvState with our captured donor block. Our write
// is now the LAST touch before the render samples — guaranteed ordering, no flicker.
//
// TRADE-OFF: restamping the whole 0x2F8 block freezes time-of-day lighting for that zone (the recompute is where sun
// angle etc. get baked). Acceptable for a cinematic-weather cram; a later refinement can restamp only the
// weather-specific sub-fields to keep live day/night under a foreign sky.
public sealed unsafe class WeatherCramService : IDisposable
{
    // Brio's signature for the per-frame EnvState recompute: nint UpdateEnvironment(EnvState* dst, EnvState* src).
    private const string UpdateEnvironmentSig = "0F 10 42 08 0F 11 41 08 F2 0F 10 4A 18";
    private const int EnvStateOffset = 0x58;
    private const int EnvStateSize = 0x2F8;

    // POINTER SANITIZATION (2026-08-16): EnvState is NOT pure floats — it embeds resource pointers/handles (proven by a
    // restamp crash: sub_1402FE240 dereferenced 0x2A5A683EE20, a donor-zone heap pointer that dangles on the target
    // zone). Fable's "pointer-free floats" read was from a float-FILTERED dump that hid the pointer words. So we restamp
    // SELECTIVELY: copy the param words, but at any 8-byte word whose CAPTURED value looks like a canonical x64
    // user-space heap pointer, leave the destination's own (valid) value untouched. This (a) kills the dangling-pointer
    // crash class and (b) is the semantically correct "sky-only" restamp — the zone keeps its real resource handles
    // (its own avfx etc.), we only override the colour/scalar sky params. A nonzero float pair almost never lands in the
    // pointer range (a param's high dword is 0 or >= ~0x3A000000, giving an 8-byte value below 2^40 or above 2^47), so
    // false-positives (a real param wrongly preserved) are negligible; the diagnostic log lists preserved offsets so
    // the exact pointer-field map can be pinned and hardcoded later.
    private const ulong PtrMin = 0x0000_0100_0000_0000UL;   // 2^40 — below any real x64 heap ptr's magnitude, above any plausible float-pair value
    private const ulong PtrMax = 0x0000_7FFF_FFFF_FFFFUL;   // 2^47-1 — top of canonical user-space

    private delegate nint UpdateEnvironmentDelegate(nint a1, nint a2);
    private readonly Hook<UpdateEnvironmentDelegate>? hook;
    private readonly IPluginLog log;

    private byte[]? captured;
    private byte capturedWeather;
    private bool loggedTarget;
    // Precomputed once per captured block: true = this 8-byte word looks like a pointer in the capture → do NOT restamp
    // it (preserve the destination). Built by BuildSkipMask; consumed per-frame in Detour.
    private bool[]? skipWord;

    // ── b160 (Path I PROVEN → KEYFRAME sky-graft: the day-night CYCLING restamp) ──────────────────────────────────
    // The b96 static restamp (wxcapture/wxreplay) PROVED (b159 in-game test) the EnvState restamp fully owns the visible
    // sky dome + stars: Kugane's clear starry night grafted onto Lapis Manalis, overriding the native grey Blizzard sky
    // ("LADIES AND GENTLEMEN, WE HAVE STARS"). But ONE captured block is FROZEN — no traveling sun, no day/night gradient.
    // This makes it CYCLE. Capture a SET of donor keyframes across the Eorzean day (each tagged with its tod-seconds),
    // travel to the fixed-time target, then per-frame LERP the two keyframes bracketing the current Eorzea tod and restamp
    // the blend. WE own the interpolation (native never re-samples our block, which is exactly the b159 freeze wall the
    // handle-swap cram hit), so a fixed-time dungeon gets a full moving-sun/gradient/stars day-night sky. Pointer-range
    // words are STILL preserved via the same skip-mask (BuildSkipMask): interpolating a heap address = garbage → dangling
    // crash. Deterministic across peers: identical keyframe set + identical tod ⇒ byte-identical blend on every client.
    public bool KfActive => kfActive;
    private bool kfActive;
    private readonly System.Collections.Generic.List<(float tod, byte[] block)> keyframes = new(); // tod = 0..86400 s-of-day, kept sorted
    // b166 FIDELITY PROBE: a dense capture snapshot (typically the 1440-sample `wxkfsweep 1` full day) kept separate from
    // the active graft set. wxkfdecim uniformly down-samples THIS into `keyframes` at a chosen N, non-destructively, so the
    // operator can A/B the SAME capture at N=240/120/60/30… without re-sweeping — the visual test for "which N looks best"
    // and the storage-fidelity trade-off. Populated lazily from `keyframes` on first decimate; cleared with the set.
    private readonly System.Collections.Generic.List<(float tod, byte[] block)> masterKeyframes = new();
    private byte kfWeather;   // donor weather the keyframe set was captured under (first capture pins it; later captures must match)

    // ── TRAVELING-SUN field map (wxtimescan) ─────────────────────────────────────────────────────────────────────
    // Read-only diagnostic: with NO cram active, sample the freshly-recomputed EnvState (a1, post-Original) each frame
    // while the operator drags the clock slider, and DISCOVER which float offsets move with time. Those offsets are the
    // time-driven sun/light/ambient/fog fields — the exact preserve-set a future "live-sun cram" would leave untouched
    // (same skip-mask trick we use for pointer words, aimed at lighting instead). Change-gated: logs an offset only the
    // first frame it moves, so a full 0→24h sweep prints a clean growing map, and `wxtimescan` again prints the summary.
    private bool scanActive;
    private float[]? scanPrev;
    private readonly System.Collections.Generic.SortedSet<int> scanMoved = new();

    // ── b119 Tier-2 (WEATHER-CRAM-MECHANISM §5 step 2/3) ──────────────────────────────────────────────────────────
    // At every live capture we now SNAPSHOT each avfx doodad descriptor (the ~0x80-byte struct at 0x2C0/0x2C8: inline
    // .avfx path + transform block) into managed bytes, plus the offsets of its INNER heap pointers. That is exactly the
    // "bake the descriptor" feedstock Tier-2 needs. `wxcoldtest` then RE-ESTABLISHES the doodad from a self-allocated
    // native buffer holding those bytes (candidate A) and re-points the EnvState word at it — the in-game test of
    // whether a persisted/synced preset (whose original donor pointer is a dead heap address) can spawn doodads by
    // shipping the descriptor bytes instead of the raw pointer.
    private sealed class CapturedDoodad
    {
        public int WordOffset;                 // EnvState offset (0x2C0/0x2C8) whose pointer word points at this descriptor
        public byte[] Bytes = Array.Empty<byte>();   // snapshot: inline path at +0x00, then transform (+ maybe inner ptrs)
        public string Path = "";
        public readonly System.Collections.Generic.List<int> InnerPtrOffsets = new();  // offsets in Bytes that look like heap ptrs
    }
    private System.Collections.Generic.List<CapturedDoodad>? capturedDoodads;
    private readonly System.Collections.Generic.List<nint> coldAllocs = new();   // self-alloc'd descriptor buffers (freed on re-test/dispose)
    private const int DoodadSnapshotLen = 0x200;   // generous window; clamped to the committed-readable extent at capture

    // ── b130 FAULT DIAG (wxdooddiag) ──────────────────────────────────────────────────────────────────────────────
    // WHY: 207 (Auroral Flares) & 208 (Floracane) are the ONLY two doodad weathers that CTD when re-established; the
    // other 96 render despite carrying dangling donor pointers too. Static analysis found NO shared discriminator (207
    // is 512B/25-ptr dense, 208 is 320B/3-ptr sparse — opposite shapes), so the cause is a RUNTIME traversal decision:
    // the env routine (sub_1402FE240) walks only a FEW descriptor offsets, and a weather crashes iff a WALKED offset
    // holds a bad pointer. The fault is C0000005 reading [RDX+0x20] on the GAME thread — uncatchable by managed
    // try/catch. A Vectored Exception Handler sees it first-chance: we record RIP/RDX/fault-addr, then match (fault-0x20)
    // back to the exact descriptor word that was walked — naming the offending offset+path.
    //
    // CAPTURE-THEN-DIE (b130 revised): the VEH writes the forensic line to a PRE-OPENED disk file (flushed) and returns
    // CONTINUE_SEARCH so the normal C0000005 crash proceeds. Persisting to disk BEFORE dying follows the wxsweep
    // precedent — /xllog lines do NOT reliably survive a hard native CTD. The earlier `survive` mode (redirect RDX to a
    // zero page + CONTINUE_EXECUTION) is REMOVED: resuming the native object-graph walk with a bogus base pointer
    // cascaded into a SILENT hard crash (worse than the clean AV). Capturing then dying is the only safe design.
    private nint vehHandle;
    private bool faultDiagArmed;
    private int faultDiagHits;
    private ulong moduleBase;
    private nint envRoutineLo, envRoutineHi;   // RIP window scoping the VEH to sub_1402FE240 (RVA 0x2FE240)
    private VectoredHandler? vehDelegate;       // rooted reference — the CLR must not GC the delegate while registered
    private System.Collections.Generic.IReadOnlyList<DoodadBake>? lastBakes;   // last re-established set, for fault->offset matching
    private string? pendingFaultChat;            // set in the VEH (cheap, thread-safe), drained to chat on the framework thread
    private System.IO.StreamWriter? faultLog;    // pre-opened at arm; VEH only Write+Flush's so the line survives the CTD
    private string faultLogPath = "";

    // b130: MapSettingsService.DoodadsAllowedFor consults this so the CrashDoodadIds gate is BYPASSED for 207/208 while
    // armed — otherwise the crashers get sky-only doodad-free crams and the VEH never sees a fault to capture.
    public bool FaultDiagArmed => faultDiagArmed;

    // b130: pull the last captured fault as a chat-ready line (null if none pending). Called each frame from
    // OnFrameworkUpdate — chat.Print must run on the framework thread, and the VEH runs on the faulting thread mid-
    // exception where touching game UI is unsafe, so the handler only stashes a string and this hands it off.
    public string? DrainFaultChat() => System.Threading.Interlocked.Exchange(ref pendingFaultChat, null);

    public bool Available => hook != null;
    public bool ReplayActive { get; private set; }

    // ── b140 (Path I: the live handle swap) ──────────────────────────────────────────────────────────────────────
    // Repoint a live EnvSpace's EnvSetResourceHandle (+0x90) at a DONOR .envb handle we loaded via GetResourceSync, so
    // the game's own UpdateEnvironment samples the donor's keyframe curves natively → full day-night cycling + skybox +
    // avfx for a weather the target map does NOT carry, with NO per-frame restamp. We hold the donor handle (IncRef) and
    // mirror its per-slot weatherId list into EnvScene.WeatherIds[32] so §11.4 resolve indexes into the donor bank. A
    // per-frame TickCycleCram re-asserts the swap in case the zone reasserts +0x90 (Principle 1). Everything is restored
    // + DecRef'd on clear. This is the FIRST Path I build that WRITES to game memory (three aligned writes: the +0x90
    // pointer, the WeatherIds bytes, the ActiveWeather byte); all are values validated by the b139 verify pass.
    public bool CycleActive => cycleActive;
    private bool cycleActive;
    private nint cycleDonorHandle;       // held (IncRef'd) donor EnvSetResourceHandle — DecRef on clear
    private nint cycleSpaceHandleAddr;   // &EnvSpace[i].EnvSetResourceHandle (the +0x90 slot we write)
    private nint cycleOrigHandle;        // the handle we displaced (zone still owns its own ref → stays alive)
    private nint cycleSceneAddr;         // EnvScene base (for WeatherIds restore)
    private byte[]? cycleOrigWeatherIds; // EnvScene.WeatherIds[32] snapshot for restore
    private byte cycleOrigActive;        // ActiveWeather snapshot for restore
    private byte cycleDonorWeather;      // the donor weather id we drive
    private string cycleDonorPath = "";
    private int cycleRevertCount;        // how many frames the zone reverted our +0x90 swap (diagnostic)
    private int cycleTickFrames;         // b141: frame counter for the throttled [WXCYCLE-T] time/transition trace
    private uint cycleLastEnvHash;       // b143: last-logged resolved-EnvState hash (detects whether the sky re-samples)
    private float cycleDetourPostDayTime = -1; // b144: DayTimeSeconds read back AFTER Original in the Detour (did the sampler re-pin our write?)
    private float cycleSceneSecs = -1;   // b146: EnvScene+0x80 (scene seconds-of-day) read back post-Original
    private float cycleSceneHour = -1;   // b146: EnvScene+0x54 (scene hours) read back post-Original
    // b142: self-driven day clock. Fixed-time zones (dungeons/interiors) PIN EnvMgr.DayTimeSeconds, so the donor
    // keyframes never get sampled across the day. When driving, we advance a virtual clock and write DayTimeSeconds
    // each frame → the native sampler travels the donor set. speed=0 disables (use the zone's own time, for live zones).
    private bool cycleDriveTime;
    private float cycleTimeSpeed;        // DayTime-seconds advanced per REAL second (0 = don't drive; 700 ≈ full day/123s)
    private float cycleVirtualDayTime;   // our advancing clock, seeded from the zone's DayTime at Start
    private long cycleLastTickMs;        // real-time delta base for the advance

    // ── b165 (freeze-probe: wxfreezeprobe) — the DECISIVE read-only test for the re-interpolate freeze ──────────────
    // The client-native question: on a fixed-time zone, when we DRIVE the clock, does the native UpdateEnvironment
    // re-interpolate EnvState (0x58) from the .envb each frame, or is it frozen (flat/single-keyframe curve, or the
    // sampler ignores our clock)? b164's snapshot A/B couldn't answer it (single-shot, confounded weather+zone+time).
    // This is the right instrument: OBSERVER hashes a1 (== EnvMgr+0x58, the freshly-interpolated block) each frame
    // post-Original and logs ONCE PER HASH CHANGE with the driven clocks; SWEEP self-advances a virtual clock across the
    // full day pre-Original (same slot as the b162 sun-clock drive, so it beats the fixed-time re-pin). Verdict = the
    // distinct-hash-change count over one sweep: ~1 ⇒ FROZEN (native cycling unavailable here → recording path stays);
    // hundreds ⇒ RE-INTERPOLATING (the sampler tracks the driven clock → the freeze was elsewhere, possibly recoverable).
    // Purely read-only aside from the clock writes the graft already performs; runs no restamp (probe never sets
    // cycleActive/kfActive). NOTE (honest caveat): a FROZEN result is ambiguous between "flat curve / sampler ignores
    // time" and "we drove the wrong clock field" — but b162 proved 0x10/EnvScene+0x80/+0x54 are the render's real time
    // inputs (they move the sun), so a frozen EnvState under a driven sweep is strong evidence for flat-curve/ignore.
    private bool freezeProbeActive;
    private bool freezeProbeSweep;       // true = self-drive the clock across the day (fixed-time zones); false = observe only (city, move time yourself)
    private float freezeProbeVirtualSecs;// the swept virtual clock (seconds-of-day, wraps 86400)
    private const float FreezeProbeStep = 300f; // secs-of-day advanced per frame (≈5 game-min/frame → full day in ~288 frames ≈ 5s @60fps)
    private uint freezeProbeLastHash;
    private bool freezeProbeHasLast;
    private int freezeProbeChanges;      // distinct-hash transitions observed (the verdict metric)
    private int freezeProbeFrames;       // total frames observed

    // ── b149 (Path I: the SKY CUBEMAP swap — the starfield fix) ──────────────────────────────────────────────────
    // wxskydiag (b148) proved the gap: the cycle cram swaps the EnvSpace ENV-PARAMS (+0x90) but the night STARFIELD +
    // sky-reflection cubemap live on the per-EnvSpace EnvLocation's OWN resource (EnvLocation+0x98 handle → resolved
    // Texture* at +0xA8), which a fixed-time dungeon authors as its own cave/black sky. This swaps that cubemap to a
    // donor CITY skybox .tex (e.g. Kugane's bg/ex2/02_est_e3/twn/e3t1/level/envl/evl6406959.tex) so night renders the
    // city's stars. Loaded via the b139-proven GetResourceSync path (args sourced off the zone's own live .tex handle);
    // we IncRef+hold the donor and write BOTH the handle (+0x98) and its resolved TextureResourceHandle.Texture
    // (@0x128 → EnvLocation+0xA8), re-asserting per frame. INDEPENDENT of the env cram so the starfield lever can be
    // tested alone (Principle 2). Fully reversible; DecRef on clear.
    public bool SkyActive => skyActive;
    private bool skyActive;
    private nint skyDonorTexHandle;    // held (IncRef'd) donor .tex TextureResourceHandle
    private nint skyLocAddr;           // EnvLocation base whose cubemap we swapped
    private nint skyOrigCubeResHandle; // original EnvLocation+0x98 (TextureResourceHandle*)
    private nint skyOrigCubeTex;       // original EnvLocation+0xA8 (resolved Texture*)
    private string skyDonorPath = "";
    private int skyRevertCount;
    private bool skyTexResolved;       // has the donor .tex's GPU Texture (@0x128) populated + been installed at +0xA8 yet?
    private int skyResolveFrames;      // frames spent waiting for the async GPU upload (diagnostic / kick cadence)
    private const int TexHandleResolvedTexOffset = 0x128; // TextureResourceHandle.Texture (resolved GPU texture)

    // ── b152 (Path I: swap the AMBIENT set — the un-swapped per-zone light resource) ──────────────────────────────
    // The env-param cram darkens the sky GRADIENT but leaves the dungeon's own bright .amb ambient — so clouds stay lit
    // white and the engine luminance never drops, which is why StarRenderer never fades stars in. This swaps the .amb.
    public bool AmbientActive => ambActive;
    private bool ambActive;
    private nint ambDonorHandle;       // held (IncRef'd) donor .amb AmbientSetResourceHandle
    private nint ambLocAddr;           // EnvLocation base whose ambient we swapped
    private nint ambOrigResHandle;     // original EnvLocation+0x90 (AmbientSetResourceHandle*)
    private nint ambOrigSet;           // original EnvLocation+0xA0 (resolved AmbientSet void*)
    private string ambDonorPath = "";
    private int ambRevertCount;
    private int ambResolvedOff = -1;   // discovered offset inside the .amb handle that holds the resolved AmbientSet ptr
    private bool ambSetResolved;

    // ── b155 (Path I: FORCE the star params) ─────────────────────────────────────────────────────────────────────
    // b154 A/B (Kugane-night vs crammed-dungeon-night) proved the StarRenderer gets our time (+0xA8 matches) and is
    // enabled (+0xC4=1), but its intensity/size block +0xB0..+0xBC is ZERO in the dungeon (Kugane-night: 0.25/2.5/5/1.178)
    // — a per-zone star routine that runs in Kugane never runs in the dungeon, so stars have zero intensity → invisible.
    // This writes Kugane's night values into those fields (+ the 0xAC sign / 0xC8 mismatch) and holds them each frame.
    // NOTE: static night values — full-brightness even at crammed noon; the time-fade curve is a follow-up once proven.
    public bool StarForceActive => starForceActive;
    private bool starForceActive;
    private nint starRendererAddr;
    private int starForceRevertCount;
    // (offset, Kugane-night value) — the fields that diverged in the b154 A/B. 0xB0..0xBC = the zeroed intensity block.
    private static readonly (int off, float val)[] StarForceVals =
        { (0xAC, 30f), (0xB0, 0.25f), (0xB4, 2.5f), (0xB8, 5f), (0xBC, 1.178f), (0xC8, 0.22f) };
    private float[] starForceOrig = System.Array.Empty<float>();

    public WeatherCramService(ISigScanner sig, IGameInteropProvider hooks, IPluginLog log)
    {
        this.log = log;
        try
        {
            var addr = sig.ScanText(UpdateEnvironmentSig);
            hook = hooks.HookFromAddress<UpdateEnvironmentDelegate>(addr, Detour);
            log.Information("[HMSync] WeatherCramService: UpdateEnvironment hook installed.");
        }
        catch (Exception ex)
        {
            hook = null;
            log.Error("[HMSync] WeatherCramService: UpdateEnvironment sig scan failed - weather cram unavailable: " + ex.Message);
        }
        // b130 wxdooddiag: capture ffxiv_dx11.exe base so the fault VEH can be scoped tightly to sub_1402FE240
        // (RVA 0x2FE240 — the crash string was ffxiv_dx11.exe+2FE246). A generous window covers the whole walk.
        try
        {
            moduleBase = (ulong)System.Diagnostics.Process.GetCurrentProcess().MainModule!.BaseAddress;
            envRoutineLo = (nint)(moduleBase + 0x2FE240);
            envRoutineHi = (nint)(moduleBase + 0x2FF040);
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] WeatherCramService: could not resolve module base for fault diag: " + ex.Message);
        }
    }

    // Run the recompute first (a1 gets the zone's own env), THEN stamp our captured donor block over a1. a1 is the
    // render-effective EnvState; a one-time log confirms it is EnvMgr+0x58 (the block we captured from).
    private nint Detour(nint a1, nint a2)
    {
        // b144: Path I time-drive lands HERE, not in Framework.Update. b143 proved the game RE-PINS DayTimeSeconds to the
        // zone's fixed value (36000s) every frame — our Framework.Update write was always dead before the sampler ran
        // (preWrite=36000 on every trace line). This Detour wraps the game's OWN per-frame time-interpolation (Original
        // reads DayTimeSeconds → writes the interpolated EnvState into a1). Writing our virtual clock at the TOP, just
        // before Original, makes it the value the sampler consumes — the last touch before interpolation, so the re-pin
        // (which runs earlier in the frame) no longer wins. We read DayTimeSeconds back AFTER Original into
        // cycleDetourPostDayTime: if Original itself re-pins it to 36000 the drive must instead patch the FIXED-TIME
        // SOURCE field (next build); if it stays at our virtual, the sky travels.
        // b162: the SUN-ANGLE clock. Drive it whenever EITHER the handle-swap cram OR the keyframe graft is live. The
        // graft restamps EnvState (sky dome + stars) off EorzeaTodSeconds, but the SUN DISC is positioned by Original
        // from the native scene clock — which a fixed-time zone pins (≈36000s ≈ 10:00), so the graft's cycling sky was
        // rendering UNDER a stuck daytime sun (the "sun at 23:59" bug). Driving the scene clock here off the SAME
        // EorzeaTodSeconds the restamp uses makes the sun travel IN PHASE with the sky — no handle swap needed (the cram
        // only ever mattered for its sun angle; its sky is overwritten by RestampInterpolated post-Original anyway).
        bool probeDrive = freezeProbeActive && freezeProbeSweep;   // b165: self-drive the clock so a fixed-time zone's clock actually moves
        if (cycleActive || (kfActive && keyframes.Count >= 2) || probeDrive)
        {
            var em = EnvManager.Instance();
            if (em != null)
            {
                // b147: the drive clock. PRODUCTION (default, cycleDriveTime=false): track the REAL Eorzea time-of-day —
                // the SAME field (ClientTime.EorzeaTime) the HMS time slider and Freeze drive — so crammed city weather
                // cycles at the natural Eorzean rate (~70min/day) AND honours the slider / Freeze, i.e. identical motion
                // and mechanism to a native city sky (the user's stated goal). DEMO (cycleDriveTime=true, [speed]>0):
                // feed our self-advanced virtual clock instead, for a fast preview on fixed-time zones. The graft path
                // (kfActive, no cycle) always tracks EorzeaTodSeconds — it has no virtual-clock demo mode.
                // b165 PROBE SWEEP: advance our own virtual clock across the full day (fast) so the freeze-probe can watch
                // whether EnvState re-interpolates as the clock travels — takes precedence when the probe is sweeping.
                float driveSecs;
                if (probeDrive)
                {
                    freezeProbeVirtualSecs += FreezeProbeStep;
                    if (freezeProbeVirtualSecs >= 86400f) freezeProbeVirtualSecs -= 86400f;
                    driveSecs = freezeProbeVirtualSecs;
                }
                else driveSecs = (cycleActive && cycleDriveTime) ? cycleVirtualDayTime : EorzeaTodSeconds();
                em->DayTimeSeconds = driveSecs;                               // 0x10 (b144 — holds; kept in sync for consistency)
                // b145 wxenvdump found the sampler's REAL clock lives on EnvScene, pinned to the zone's fixed time while
                // 0x10 advanced: EnvScene+0x080 = seconds-of-day, EnvScene+0x054 = same time in hours. Drive BOTH here,
                // each in its own unit, in this pre-Original slot so the re-write beats whatever pins them earlier in the
                // frame — then Original interpolates the sky off these and a fixed-time zone travels.
                var sc = em->EnvScene;
                if (sc != null)
                {
                    nint scb = (nint)sc;
                    if (IsReadable(scb + 0x80, 4)) *(float*)(scb + 0x80) = driveSecs;          // seconds-of-day
                    if (IsReadable(scb + 0x54, 4)) *(float*)(scb + 0x54) = driveSecs / 3600f;  // hours
                }
            }
        }
        var ret = hook!.Original(a1, a2);
        // b165 freeze-probe observer: a1 now holds the NATIVE sampler's freshly-interpolated EnvState (before any restamp;
        // the probe runs no restamp). Hash it and log ONLY when the hash changes, alongside the driven clocks — so a
        // full-day sweep prints a dense trail of changes if the sampler re-interpolates, or falls silent after the first
        // frame if EnvState is frozen. The change count at STOP is the verdict.
        if (freezeProbeActive && a1 != 0)
        {
            freezeProbeFrames++;
            uint h = HashBlock(a1, EnvStateSize);
            if (!freezeProbeHasLast || h != freezeProbeLastHash)
            {
                freezeProbeChanges++;
                freezeProbeHasLast = true;
                freezeProbeLastHash = h;
                var em = EnvManager.Instance();
                float dts = em != null ? em->DayTimeSeconds : -1f;
                float ss = -1f, sh = -1f;
                if (em != null && em->EnvScene != null)
                {
                    nint scb = (nint)em->EnvScene;
                    if (IsReadable(scb + 0x80, 4)) ss = *(float*)(scb + 0x80);
                    if (IsReadable(scb + 0x54, 4)) sh = *(float*)(scb + 0x54);
                }
                log.Information("[HMSync] [WXFREEZEPROBE] change#" + freezeProbeChanges + " frame=" + freezeProbeFrames
                    + " drive0x10=" + dts.ToString("0") + " es+0x80=" + ss.ToString("0") + " es+0x54=" + sh.ToString("0.##")
                    + " hash=0x" + h.ToString("X8"));
            }
        }
        if (cycleActive)
        {
            var em = EnvManager.Instance();
            if (em != null)
            {
                cycleDetourPostDayTime = em->DayTimeSeconds;
                var sc = em->EnvScene;                                          // b146: did Original re-pin the scene clock?
                cycleSceneSecs = (sc != null && IsReadable((nint)sc + 0x80, 4)) ? *(float*)((nint)sc + 0x80) : -1;
                cycleSceneHour = (sc != null && IsReadable((nint)sc + 0x54, 4)) ? *(float*)((nint)sc + 0x54) : -1;
            }
        }
        try
        {
            // wxtimescan: a1 now holds the game's freshly time-interpolated EnvState — the ideal sample point. Runs
            // independently of ReplayActive (the scan wants the UNcrammed native env, so it's used with no cram on).
            if (scanActive && a1 != 0) ScanDiff(a1);
            if (ReplayActive && captured != null && a1 != 0)
            {
                if (!loggedTarget)
                {
                    loggedTarget = true;
                    var env = EnvManager.Instance();
                    nint es = env != null ? (nint)env + EnvStateOffset : 0;
                    log.Information("[HMSync] [WXCRAM] restamp target a1=0x" + a1.ToString("X")
                        + " EnvMgr+0x58=0x" + es.ToString("X") + " match=" + (a1 == es));
                }
                RestampSelective(a1);
            }
            // b160: keyframe day-night graft — lerp the two keyframes bracketing the current tod and restamp the blend.
            // Independent of the static ReplayActive path (this owns its own kfActive gate + >=2 keyframes).
            if (kfActive && keyframes.Count >= 2 && a1 != 0) RestampInterpolated(a1);
        }
        catch { }
        return ret;
    }

    // WHOLESALE-vs-SELECTIVE decision (b114): choose the restamp mode for a freshly-set captured block by its weather.
    //   • AVFX-SAFE weathers (MapSettingsService.AvfxSafeWeatherIds, e.g. 150 Apocalypse) → skipWord = null → WHOLESALE
    //     restamp (copy the donor block verbatim, INCLUDING its pointer/handle words). This is the b96 behaviour that
    //     produced the "final days on Limsa" meteors: the donor's EnvState carries the weather's avfx RESOURCE HANDLE,
    //     and replaying that word verbatim re-plants it on the target so the particle system spawns the doodads. It is
    //     the exact thing the b100 selective restamp threw away to stop the weather-5 dangling-pointer crash. Gated to
    //     the allow-list precisely because a donor pointer CAN dangle on the target (150's stays valid same-session; 5's
    //     did not → CTD) — the allow-list is the "safe to replay wholesale" set.
    //   • everything else → BuildSkipMask → SELECTIVE restamp (sky-only, preserves the destination's own handles, no
    //     dangling-pointer crash). The crash-free default.
    // NOTE: wholesale replay of a PERSISTED/shipped preset is fragile (the baked pointer is a stale heap address from
    // bake time); the proven path is a SAME-SESSION live capture (wxcapture on the donor → travel → wxreplay).
    // `live` = this block was just captured THIS session (Capture/CaptureRaw) — its donor pointer word is still a valid
    // in-process handle, so wholesale replay is safe. A PERSISTED library blob (ApplyBlob) passes live=false: its pointer
    // is a stale bake-time address that would dangle → force selective even for avfx-safe ids (sky-only, no doodads).
    private void ConfigureRestampFor(byte weather, byte[] block, bool live)
    {
        if (live && MapSettingsService.AvfxSafeWeatherIds.Contains(weather))
        {
            skipWord = null;   // wholesale: keep the donor's avfx handle so the doodads spawn
            log.Information("[HMSync] [WXCRAM] restamp mode = WHOLESALE for avfx-safe weather " + weather
                + " (live capture — donor handles preserved → doodads).");
            // Tier-2 enabler (b115): wholesale skips BuildSkipMask, so log the donor block's pointer-range word offsets
            // here (LOG-ONLY, no masking). One of these words is this weather's avfx RESOURCE HANDLE — comparing the set
            // across a couple of donor captures (and against a zone's own idle block) pins the avfx-handle OFFSET, which
            // is what a persisted/shipped preset would need to RE-RESOLVE by path at replay (so doodads survive past a
            // same-session capture). See project_hms_geometry_poc Tier-2.
            LogPointerWords(block, weather);
        }
        else
        {
            BuildSkipMask(block);   // selective: sky-only, crash-safe (all persisted blobs + non-avfx-safe live captures)
        }
    }

    // SELECTIVE restamp: copy the captured block over a1 word-by-word, but SKIP any 8-byte word the mask flags as a
    // pointer in the capture (skipWord[i] == true) — leaving the destination's own valid handle intact. This is the
    // fix for the wxpreset dangling-pointer crash (a donor-zone heap pointer restamped onto a foreign zone faulted the
    // env-resource routine). skipWord == null ⇒ WHOLESALE copy (avfx-safe weathers, b114 — see ConfigureRestampFor).
    private void RestampSelective(nint a1)
    {
        var cap = captured;
        var mask = skipWord;
        if (cap == null) return;
        if (mask == null || mask.Length * 8 < EnvStateSize)
        {
            Marshal.Copy(cap, 0, a1, EnvStateSize);
            return;
        }
        int words = EnvStateSize / 8;                  // 95 whole 8-byte words
        for (int i = 0; i < words; i++)
        {
            if (mask[i]) continue;                     // preserve destination at pointer-range words
            Marshal.Copy(cap, i * 8, a1 + i * 8, 8);
        }
        // Tail bytes (EnvStateSize is 0x2F8 = 760 = 95*8 exactly, so none) — guarded for layout drift.
        int tail = EnvStateSize - words * 8;
        if (tail > 0) Marshal.Copy(cap, words * 8, a1 + words * 8, tail);
    }

    // ── b160: KEYFRAME sky-graft (day-night cycling) ─────────────────────────────────────────────────────────────
    // Capture the live EnvState as a keyframe tagged with the current Eorzea tod. Call in the DONOR zone (e.g. Kugane),
    // under the target weather, at several times across the day — Freeze the clock and drag the HMS time slider to each
    // hour, capturing at each. The first capture pins the donor weather + builds the pointer skip-mask; later captures
    // must be the same weather. A re-capture within a minute of an existing keyframe REPLACES it (re-shoot an hour).
    public string AddKeyframe()
    {
        try
        {
            var env = EnvManager.Instance();
            if (env == null) return "[HMSync] wxkfcap: EnvManager null — in a zone?";
            byte active = env->ActiveWeather;
            if (keyframes.Count > 0 && active != kfWeather)
                return "[HMSync] wxkfcap: weather mismatch — the keyframe set is weather " + kfWeather + " but you're under "
                    + active + ". Set the donor weather back, or `wxkfclear` to start a new set.";
            bool first = keyframes.Count == 0;
            float tod = EorzeaTodSeconds();               // 0..86400 seconds-of-day
            var buf = new byte[EnvStateSize];
            Marshal.Copy((nint)env + EnvStateOffset, buf, 0, EnvStateSize);
            int dup = keyframes.FindIndex(k => Math.Abs(k.tod - tod) < 60f);
            if (dup >= 0) keyframes[dup] = (tod, buf);     // re-shoot of the same hour → overwrite, don't pile up
            else keyframes.Add((tod, buf));
            keyframes.Sort((x, y) => x.tod.CompareTo(y.tod));
            if (first) kfWeather = active;
            BuildSkipMaskUnion(keyframes);                 // b177: rebuild union each capture — a mid-day pointer added later must be preserved too
            float h = tod / 3600f;
            return "[HMSync] wxkfcap: keyframe @ " + h.ToString("00.0") + "h (weather " + active + ") — now "
                + keyframes.Count + " keyframe(s). Capture more across the day, travel to the target, then `wxkfreplay on`.";
        }
        catch (Exception ex) { return "[HMSync] wxkfcap failed: " + ex.Message; }
    }

    // Toggle the keyframe day-night graft on the CURRENT zone. Needs >=2 keyframes. Enables the shared hook while active.
    public string SetKfReplay(bool? on = null)
    {
        if (hook == null) return "[HMSync] wxkfreplay: UpdateEnvironment hook unavailable (sig scan failed at ctor).";
        if (keyframes.Count < 2)
            return "[HMSync] wxkfreplay: need at least 2 keyframes (have " + keyframes.Count
                + ") — capture across the day with `wxkfcap` first.";
        bool want = on ?? !kfActive;
        kfActive = want;
        if (want) loggedTarget = false;
        SyncHookState();
        return "[HMSync] wxkfreplay: " + (want ? "ON" : "OFF") + " — " + keyframes.Count
            + " keyframes, weather " + kfWeather + ". Sky now cycles with the Eorzea clock (drive it with the slider).";
    }

    // b166 FIDELITY PROBE (dev-only, not exposed as a command in release): non-destructively replay the DENSE captured set at a reduced keyframe
    // count N, so the operator can eyeball where banding appears and pick the storage/fidelity sweet spot. First call
    // snapshots the current (dense) `keyframes` into `masterKeyframes`; thereafter every call re-samples the MASTER
    // uniformly (endpoints always kept) into the active graft set — so N can be dialed up and down freely without losing
    // the full-density capture. `full` restores every master sample. Reports byte size and a reconstruction RMS error vs
    // the dense master (a RELATIVE figure: compare it across N — it should plateau once N is dense enough to catch the
    // transitions; the visual banding is the primary judge, the number is the companion). Graft state is left as-is; if
    // it's already ON the swap is live, otherwise `wxkfreplay on` after.
    public string DecimateForReplay(string arg)
    {
        if (masterKeyframes.Count == 0)
        {
            if (keyframes.Count < 2)
                return "[HMSync] wxkfdecim: no dense set in memory — run `wxkfsweep 1` (full-day 1440-sample capture) in the "
                    + "donor first, then decimate.";
            foreach (var k in keyframes) masterKeyframes.Add(k);   // pin the dense capture as the reference master
        }
        int master = masterKeyframes.Count;
        string a = (arg ?? "").Trim().ToLowerInvariant();
        int n;
        if (a == "full" || a == "0" || a.Length == 0) n = master;
        else if (!int.TryParse(a, out n) || n < 2)
            return "[HMSync] wxkfdecim: usage `wxkfdecim <N|full>` (N>=2). Master holds " + master + " keyframes.";
        if (n > master) n = master;

        var pick = new System.Collections.Generic.List<(float tod, byte[] block)>(n);
        if (n >= master) pick.AddRange(masterKeyframes);
        else
            for (int i = 0; i < n; i++)                            // uniform indices, first & last always included
            {
                int idx = (int)Math.Round((double)i * (master - 1) / (n - 1));
                pick.Add(masterKeyframes[idx]);
            }

        keyframes.Clear();
        keyframes.AddRange(pick);
        keyframes.Sort((x, y) => x.tod.CompareTo(y.tod));
        BuildSkipMaskUnion(keyframes);                            // rebuild the pointer-preserve mask (union across ALL keyframes)

        double rms = ReconRmsError(keyframes);
        double kb = (double)keyframes.Count * EnvStateSize / 1024.0;
        return "[HMSync] wxkfdecim: replaying " + keyframes.Count + "/" + master + " keyframes ("
            + kb.ToString("0.0") + " KB, RMS-vs-dense " + rms.ToString("0.#####") + ") — "
            + (kfActive ? "graft LIVE, re-run with another N to compare." : "now `wxkfreplay on` to view.");
    }

    // Reconstruction error of a candidate (decimated) keyframe set against the dense master: for every master sample, lerp
    // the candidate at that tod and accumulate the squared per-float delta over the non-pointer (sky) words, then RMS. A
    // pure fidelity yardstick — 0 means the candidate reproduces the dense day exactly at every master tod.
    private double ReconRmsError(System.Collections.Generic.List<(float tod, byte[] block)> approx)
    {
        if (masterKeyframes.Count == 0 || approx.Count < 2) return 0;
        var mask = skipWord;
        int words = EnvStateSize / 8;
        double sumSq = 0; long cnt = 0;
        foreach (var (tod, mblock) in masterKeyframes)
        {
            BracketKf(approx, tod, out var lo, out var up, out float t);
            for (int w = 0; w < words; w++)
            {
                if (mask != null && w < mask.Length && mask[w]) continue;
                int off = w * 8;
                for (int k = 0; k < 2; k++)
                {
                    int fo = off + k * 4;
                    float fa = BitConverter.ToSingle(lo, fo);
                    float fb = BitConverter.ToSingle(up, fo);
                    float fv = fa + (fb - fa) * t;
                    if (!float.IsFinite(fv)) fv = fa;
                    double d = fv - BitConverter.ToSingle(mblock, fo);
                    sumSq += d * d; cnt++;
                }
            }
        }
        return cnt > 0 ? Math.Sqrt(sumSq / cnt) : 0;
    }

    // Bracket a tod within a sorted keyframe set (mirrors RestampInterpolated's bracket, including the midnight wrap) and
    // return the two blocks + the 0..1 blend. Factored out so ReconRmsError shares the EXACT interpolation geometry the
    // live graft uses — the error metric must measure the same lerp the operator will see.
    private static void BracketKf(System.Collections.Generic.List<(float tod, byte[] block)> kfs, float tod,
        out byte[] a, out byte[] b, out float t)
    {
        int n = kfs.Count;
        int hi = 0;
        while (hi < n && kfs[hi].tod <= tod) hi++;
        if (hi == 0 || hi == n)
        {
            var lo = kfs[n - 1]; var up = kfs[0];
            float span = (86400f - lo.tod) + up.tod;
            float pos = (hi == 0) ? (tod + (86400f - lo.tod)) : (tod - lo.tod);
            t = span > 0.001f ? pos / span : 0f;
            a = lo.block; b = up.block;
        }
        else
        {
            var lo = kfs[hi - 1]; var up = kfs[hi];
            float span = up.tod - lo.tod;
            t = span > 0.001f ? (tod - lo.tod) / span : 0f;
            a = lo.block; b = up.block;
        }
        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
    }

    // Drop the whole keyframe set (and stop the graft if running).
    public string ClearKeyframes()
    {
        int had = keyframes.Count;
        keyframes.Clear();
        masterKeyframes.Clear();   // b166: drop the dense decimation snapshot too (a fresh sweep rebuilds it)
        if (kfActive) { kfActive = false; SyncHookState(); }
        return "[HMSync] wxkfclear: cleared " + had + " keyframe(s); graft off.";
    }

    // b162: stop the graft but KEEP the captured set. Used by the zone-change teardown — the restamped EnvState belongs
    // to the zone the graft was turned on in, so a native hop must drop it (like ReplayActive/CycleActive/etc.), but the
    // keyframe set must survive so the capture-in-donor → travel-to-target flow still works. No count guard (unlike
    // SetKfReplay, which refuses to even toggle with <2 keyframes) — turning OFF must always succeed. Returns true if it
    // was actually on, so the caller can log the auto-clear.
    public bool StopKfGraft()
    {
        if (!kfActive) return false;
        kfActive = false;
        SyncHookState();
        return true;
    }

    // PATH I b163: expose the in-memory keyframe set for disk persistence (KeyframeSetStore). Returns the donor weather
    // it was captured under and the live (tod, block) list (the store copies it). false ⇒ nothing graftable to save.
    public bool ExportKeyframes(out byte weather, out System.Collections.Generic.List<(float tod, byte[] block)> kfs)
    {
        weather = kfWeather;
        kfs = keyframes;
        return keyframes.Count >= 2;
    }

    // PATH I b163: load a saved set into memory (replaces the current one). Pins the donor weather and rebuilds the
    // pointer-preserve skip mask from the first block, exactly like a fresh first capture. Does NOT enable the graft —
    // the operator turns it on with `wxkfreplay on` after arriving at the target (same flow as a live sweep).
    public string ImportKeyframes(byte weather, System.Collections.Generic.IReadOnlyList<(float tod, byte[] block)> src)
    {
        if (src == null || src.Count < 2)
            return "[HMSync] wxkfload: set needs at least 2 keyframes (have " + (src?.Count ?? 0) + ").";
        keyframes.Clear();
        foreach (var (tod, block) in src) keyframes.Add((tod, block));
        keyframes.Sort((x, y) => x.tod.CompareTo(y.tod));
        kfWeather = weather;
        BuildSkipMaskUnion(keyframes);
        return "[HMSync] wxkfload: loaded " + keyframes.Count + " keyframes (weather " + weather
            + ") into memory. Travel to the target + `wxkfreplay on`.";
    }

    // Per-frame: lerp the two keyframes bracketing the current Eorzea tod (wrapping across midnight) and restamp the blend
    // over a1. Pointer-range words are PRESERVED (interpolating a heap address = dangling crash). Every non-pointer word is
    // two floats — blended independently. Fields that are identical in both keyframes (static flags, unchanged params)
    // reproduce exactly (a + (a-a)*t = a) regardless of whether they're really floats, so only the time-varying sky floats
    // actually blend. Direct pointer writes into the live EnvState (a1) — no per-frame allocation.
    private void RestampInterpolated(nint a1)
    {
        var kfs = keyframes;
        int n = kfs.Count;
        if (n < 2) return;
        float tod = EorzeaTodSeconds();

        // bracket: hi = first keyframe strictly after tod. hi==0 (before first) and hi==n (after last) both wrap midnight.
        int hi = 0;
        while (hi < n && kfs[hi].tod <= tod) hi++;
        byte[] a, b; float t;
        if (hi == 0 || hi == n)
        {
            var lo = kfs[n - 1]; var up = kfs[0];
            float span = (86400f - lo.tod) + up.tod;                       // the gap across midnight
            float pos = (hi == 0) ? (tod + (86400f - lo.tod)) : (tod - lo.tod);
            t = span > 0.001f ? pos / span : 0f;
            a = lo.block; b = up.block;
        }
        else
        {
            var lo = kfs[hi - 1]; var up = kfs[hi];
            float span = up.tod - lo.tod;
            t = span > 0.001f ? (tod - lo.tod) / span : 0f;
            a = lo.block; b = up.block;
        }
        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;

        // b176 LIVE pointer-guard (fixes the b175 city-variant crash). The static skipWord mask is built from ONE capture
        // (keyframes[0]) at 8-byte-aligned granularity, so it misses a handle field MISALIGNED to the word grid: a pointer
        // straddling two aligned words reads as two non-pointer halves, escapes the mask, and the float blend stomps its low
        // dword. Observed: a live Tuliyollal env-sound handle 0x190BA4B7188 had its low dword overwritten → 0x190FFFFFFFF
        // (high dword 0x190 intact, low dword gone), which EnvSoundState then dereferenced → AV at sub_1402FE246. Fix: each
        // frame, scan the LIVE EnvState at 4-byte stride and mark every float slot overlapping a live pointer-band 8-byte
        // value — never blend over it. Self-correcting (reads real memory, not a stale donor capture) and alignment-agnostic;
        // sky floats have raw bits far outside [PtrMin,PtrMax], so no visible blend is ever lost to a false positive.
        Span<bool> livePtr = stackalloc bool[EnvStateSize / 4];
        for (int o = 0; o + 8 <= EnvStateSize; o += 4)
        {
            ulong lv = *(ulong*)(a1 + o);
            if (lv >= PtrMin && lv <= PtrMax) { livePtr[o >> 2] = true; livePtr[(o >> 2) + 1] = true; }
        }

        var mask = skipWord;
        int words = EnvStateSize / 8;
        for (int w = 0; w < words; w++)
        {
            if (mask != null && w < mask.Length && mask[w]) continue;      // preserve destination pointer word (static mask)
            int off = w * 8;
            for (int k = 0; k < 2; k++)                                    // two floats per 8-byte word
            {
                int fo = off + k * 4;
                if (livePtr[fo >> 2]) continue;                            // b176: preserve any slot overlapping a live pointer
                float fa = BitConverter.ToSingle(a, fo);
                float fb = BitConverter.ToSingle(b, fo);
                float fv = fa + (fb - fa) * t;
                if (!float.IsFinite(fv)) fv = fa;                          // don't write NaN/Inf into a non-float slot
                *(float*)(a1 + fo) = fv;
            }
        }
    }

    // wxtimescan toggle. ON: clears state, arms the per-frame diff (drag the clock slider to discover fields). OFF:
    // prints the accumulated preserve-set — the float offsets that moved with time = the traveling-sun/lighting fields.
    // b136: the UpdateEnvironment hook is SHARED by the cram (wxreplay) and the time-scan (wxtimescan). Keep it enabled
    // while EITHER wants it. The bug this fixes: previously ONLY wxreplay toggled the hook, and ToggleTimeScan just set
    // scanActive without enabling it — so wxtimescan, whose own prereq told you to run with the cram OFF (which DISABLES
    // the hook), left the hook DOWN. Detour never ran, ScanDiff never ran, scanMoved stayed empty → "No moving float
    // offsets seen" on a zone (Kugane) with an obviously dramatic day/night sky. The tool disabled the very thing it
    // needed. Note ScanDiff samples a1 AFTER Original() but BEFORE RestampSelective, so a cram may stay on during a scan
    // (it reads the native time-interpolated env, not the crammed overwrite).
    private void SyncHookState()
    {
        if (hook == null) return;
        // b144: the cycle cram now also needs the hook LIVE — it drives DayTimeSeconds inside the Detour (before Original)
        // to beat the game's per-frame re-pin. Note the Detour's RestampSelective block is gated on ReplayActive (false
        // during a cycle cram), so enabling the hook for cycling does NOT restamp/freeze the sky — it only time-drives.
        bool want = scanActive || ReplayActive || cycleActive || kfActive || freezeProbeActive; // b165: probe needs the hook live to observe/drive
        if (want && !hook.IsEnabled) hook.Enable();
        else if (!want && hook.IsEnabled) hook.Disable();
    }

    public string ToggleTimeScan()
    {
        if (hook == null) return "[HMSync] wxtimescan: UpdateEnvironment hook unavailable (sig scan failed at ctor).";
        scanActive = !scanActive;
        SyncHookState();
        if (scanActive)
        {
            scanPrev = null;
            scanMoved.Clear();
            return "[HMSync] wxtimescan: ARMED (hook live). Enable Freeze and drag the clock slider across a full day "
                + "(a cram may stay ON — the scan samples the native env BEFORE any restamp). Each new time-driven float "
                + "offset is logged to /xllog [WXTIMESCAN] as it moves. Run `wxtimescan` again to stop and print the map.";
        }
        if (scanMoved.Count == 0)
            return "[HMSync] wxtimescan: stopped. No moving float offsets seen — did you sweep the clock with the cram OFF?";
        var offs = string.Join(", ", System.Linq.Enumerable.Select(scanMoved, o => "0x" + o.ToString("X")));
        log.Information("[HMSync] [WXTIMESCAN] MAP: " + scanMoved.Count + " time-driven float offset(s) in EnvState [" + offs + "]");
        return "[HMSync] wxtimescan: stopped. " + scanMoved.Count + " time-driven float offset(s): " + offs
            + "  — these are the sun/light/fog preserve-set (full list in /xllog [WXTIMESCAN]).";
    }

    // Per-frame diff of the freshly-recomputed EnvState. Discovers each float offset the game moves as the clock changes.
    // Filters to finite floats in a sane range so the pointer words (0x2C0/0x2C8, garbage-as-float) can't register.
    private void ScanDiff(nint a1)
    {
        int n = EnvStateSize / 4;                       // 190 floats across the 0x2F8 block
        var cur = new float[n];
        Marshal.Copy(a1, cur, 0, n);
        var prev = scanPrev;
        if (prev != null)
        {
            var newly = new System.Collections.Generic.List<int>();
            for (int i = 0; i < n; i++)
            {
                float a = prev[i], b = cur[i];
                if (!IsSaneParam(a) || !IsSaneParam(b)) continue;
                if (Math.Abs(b - a) <= 0.001f) continue;
                int off = i * 4;
                if (scanMoved.Add(off)) newly.Add(i);
            }
            if (newly.Count > 0)
            {
                var env = EnvManager.Instance();
                float t = env != null ? *(float*)((nint)env + 0x10) : -1f;   // DayTimeSeconds, for correlation
                var sb = new System.Text.StringBuilder();
                foreach (var i in newly)
                    sb.Append(" 0x").Append((i * 4).ToString("X")).Append('=')
                      .Append(prev[i].ToString("0.###")).Append("->").Append(cur[i].ToString("0.###"));
                log.Information("[HMSync] [WXTIMESCAN] t=" + t.ToString("0") + "s +" + newly.Count
                    + " new (total " + scanMoved.Count + "):" + sb);
            }
        }
        scanPrev = cur;
    }

    private static bool IsSaneParam(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) < 1.0e4f;

    // Tier-2 diagnostic (b115): log the pointer-range word offsets/values of a block WITHOUT building a skip mask (used
    // by the wholesale path, which preserves them). No behaviour change — pure instrumentation to pin the avfx-handle
    // offset. Note the capture-side pointers are DONOR-zone heap addresses (valid in-process this session).
    private void LogPointerWords(byte[] block, byte weather)
    {
        int words = block.Length / 8;
        var sb = new System.Text.StringBuilder();
        int n = 0;
        for (int i = 0; i < words; i++)
        {
            ulong v = BitConverter.ToUInt64(block, i * 8);
            if (v >= PtrMin && v <= PtrMax)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append("0x").Append((i * 8).ToString("X")).Append("=0x").Append(v.ToString("X"));
                n++;
            }
        }
        log.Information("[HMSync] [WXCRAM] wholesale donor pointer words for weather " + weather + ": " + n + " ["
            + sb + "] — one of these is the avfx handle (Tier-2 offset-pin).");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    // AVFX-HANDLE PROBE (b116, Tier-2 data-collection). At EVERY live capture (wxcapture / wxbakeall / wxbaketour),
    // walk the EnvState's pointer-range words, treat each as a Client::System::Resource::Handle::ResourceHandle*, and
    // read its Category (uint @+0x08; Vfx=8) and FileName (StdString @+0x48). This DUMPS, across every weather on every
    // donor map an unattended `wxbaketour` visits, exactly which EnvState offset holds the weather's avfx handle and
    // the .avfx resource PATH — the two facts Tier-2 needs to bake a path and re-resolve a fresh handle at replay.
    // CRASH-SAFE: a pointer-range word can be a FALSE POSITIVE (e.g. 0x2D0=0x10040000000, a packed scalar, not a heap
    // pointer). Dereferencing that would be an uncatchable access violation, so every read is gated by IsReadable()
    // (VirtualQuery: region must be committed + readable + large enough). Log-only; no behaviour change.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    private const int ResourceHandleSize = 0xB0;   // FFXIVClientStructs ResourceHandle (verified _GitHub\FFXIVClientStructs-main)
    private const int RhCategoryOffset = 0x08;      // ResourceHandle.Type (Category = low ushort; Vfx = 8)
    private const int RhFileNameOffset = 0x48;      // ResourceHandle.FileName (StdString: [0]=buf/ptr union, [+0x10]=size, [+0x18]=capacity)

    private void ProbeResourceWords(byte[] block, byte weather)
    {
        try
        {
            int words = block.Length / 8;
            var sb = new System.Text.StringBuilder();
            int probed = 0;
            capturedDoodads = new System.Collections.Generic.List<CapturedDoodad>();   // b119: refresh per capture
            for (int i = 0; i < words; i++)
            {
                ulong v = BitConverter.ToUInt64(block, i * 8);
                if (v < PtrMin || v > PtrMax) continue;
                probed++;
                int off = i * 8;
                nint h = (nint)v;
                if (!IsReadable(h, ResourceHandleSize))
                {
                    sb.Append("\n    0x").Append(off.ToString("X")).Append(" ptr=0x").Append(v.ToString("X"))
                      .Append(" <unreadable — false-positive scalar, not a live handle>");
                    continue;
                }
                // b118: SOLVED (b117 raw dump) — 0x2C0/0x2C8 are NOT ResourceHandle*, they point DIRECTLY at an avfx
                // doodad descriptor whose FIRST bytes are the null-terminated `.avfx` resource path (then a transform
                // block: position/heading/scale floats). The b116 `cat=0x6E2F` was just bytes 8-9 of "bgcommon/nature/…"
                // read as if +0x08 were a handle Type. So read the path straight off the target pointer.
                string avfx = TryReadCStringAt(h, 256) ?? "";
                sb.Append("\n    0x").Append(off.ToString("X")).Append(" ptr=0x").Append(v.ToString("X"));
                bool hasPath = avfx.Length > 0 && avfx.IndexOf('/') >= 0;
                if (hasPath)
                    sb.Append(" avfxpath=\"").Append(avfx).Append('"');
                else
                    sb.Append(" (no path string at +0x00)");
                // Keep the raw 0x80 window: the transform block after the path (position/heading≈-π/scale) is Tier-2
                // feedstock for reproducing the doodad's placement, not just its identity.
                sb.Append(DumpRawAt(h, 0x80));
                // b119: snapshot the descriptor bytes (bake feedstock) + note its inner heap pointers (the words that
                // would DANGLE if this preset were persisted — the thing `wxcoldtest strip` zeroes to test re-resolution).
                if (hasPath)
                {
                    var d = SnapshotDoodad(h, off, avfx);
                    if (d != null)
                    {
                        capturedDoodads.Add(d);
                        sb.Append("\n      [snapshot ").Append(d.Bytes.Length).Append(" bytes, inner ptr offsets: ")
                          .Append(d.InnerPtrOffsets.Count == 0 ? "none"
                              : string.Join(",", d.InnerPtrOffsets.ConvertAll(o => "0x" + o.ToString("X"))))
                          .Append(']');
                    }
                }
            }
            log.Information("[HMSync] [WXPROBE] weather " + weather + ": " + probed + " pointer word(s), "
                + (capturedDoodads?.Count ?? 0) + " doodad descriptor(s) snapshotted" + sb);
        }
        catch (Exception ex) { log.Warning("[HMSync] [WXPROBE] weather " + weather + " probe failed: " + ex.Message); }
    }

    // b119: snapshot a live avfx doodad descriptor into managed bytes (the "bake the descriptor" step). Reads up to
    // DoodadSnapshotLen bytes, clamped in 0x10 steps to the committed-readable extent so it never faults, and records
    // the offsets of any inner 8-byte word that looks like a heap pointer — those are the resolved-resource pointers
    // that DANGLE once the preset is persisted (what candidate-A `strip` mode zeroes to force path re-resolution).
    private CapturedDoodad? SnapshotDoodad(nint h, int off, string path)
    {
        try
        {
            int safe = 0;
            while (safe + 0x10 <= DoodadSnapshotLen && IsReadable(h + safe, 0x10)) safe += 0x10;
            if (safe == 0) return null;
            var bytes = new byte[safe];
            Marshal.Copy(h, bytes, 0, safe);
            var d = new CapturedDoodad { WordOffset = off, Bytes = bytes, Path = path };
            for (int j = 0; j + 8 <= safe; j += 8)
            {
                ulong w = BitConverter.ToUInt64(bytes, j);
                if (w >= PtrMin && w <= PtrMax) d.InnerPtrOffsets.Add(j);
            }
            return d;
        }
        catch { return null; }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    // b119 Tier-2 EXPERIMENT — candidate A (WEATHER-CRAM-MECHANISM §5 step 3). Re-establish this weather's avfx
    // doodads from SELF-ALLOCATED native descriptors instead of the donor pointer that dangles once a preset is
    // persisted/synced. For each doodad snapshotted at capture: allocate our own buffer holding the baked bytes,
    // optionally ZERO its inner heap-pointer words (`strip` — simulates the persisted case where the game's resolved
    // resource pointers are dead, testing whether the game RE-RESOLVES from the inline .avfx path), then overwrite the
    // captured EnvState word to point at OUR buffer and force WHOLESALE restamp. Operator then `wxreplay on` on a zone
    // lacking the weather: meteors spawning from our descriptor ⇒ candidate A is viable ⇒ Tier-2 bakes the descriptor
    // bytes into the preset and re-establishes at replay (so persisted/synced presets carry doodads, and the
    // AvfxSafeWeatherIds allow-list can widen past {150}).
    //   `wxcoldtest`        faithful copy — proves the game reads the doodad from an address WE own (necessary first step)
    //   `wxcoldtest strip`  inner ptrs zeroed — the real persisted-case test (does it re-resolve from the path alone?)
    // RISK (documented in §5): if the game FREES our buffer as if it owned it, that's heap corruption → only test on an
    // avfx-safe id (150). Buffers are tracked and freed on the next test / on dispose.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    public string ColdReplayTest(bool strip)
    {
        if (hook == null) return "[HMSync] wxcoldtest: UpdateEnvironment hook unavailable (sig scan failed at ctor).";
        if (captured == null) return "[HMSync] wxcoldtest: nothing captured — `wxcapture` under the donor weather first.";
        var baked = GetCapturedDoodads();
        if (baked.Count == 0)
            return "[HMSync] wxcoldtest: no avfx doodad descriptors captured for weather " + capturedWeather
                + " (this weather carries no doodads, or the capture missed them). Re-`wxcapture` under a doodad weather (e.g. 150).";
        int n = ReestablishDoodads(baked, strip, "WXCOLD");
        return "[HMSync] wxcoldtest: re-pointed " + n + " doodad descriptor(s) to self-allocated buffers ("
            + (strip ? "inner ptrs ZEROED — persisted-case test" : "faithful copy — reads-our-address test")
            + "). Now `wxreplay on` on a zone lacking weather " + capturedWeather
            + " — meteors ⇒ candidate A works. Details in /xllog [WXCOLD].";
    }

    // b120: PRODUCTION re-establish — the shared core behind both the persisted-preset apply path (ApplyBlob) and the
    // manual `wxcoldtest`. For each baked doodad, allocate a LIVE native buffer holding its descriptor bytes, optionally
    // ZERO the inner heap-pointer words (`strip`, diagnostic only — production keeps a faithful copy), overwrite the
    // captured EnvState word to point at OUR buffer, and force WHOLESALE restamp so the re-pointed words land verbatim.
    // Returns the count re-established. Buffers tracked in coldAllocs (freed on next re-establish / dispose).
    private int ReestablishDoodads(System.Collections.Generic.IReadOnlyList<DoodadBake> doods, bool strip, string tag)
    {
        if (captured == null) return 0;
        FreeColdAllocs();   // release prior buffers; replay is (re-)armed by the caller, so nothing points at them yet
        lastBakes = doods;  // b130: record for wxdooddiag — the VEH matches a fault's bad pointer to a word in these bytes
        var sb = new System.Text.StringBuilder();
        int n = 0;
        foreach (var d in doods)
        {
            if (d.Bytes == null || d.Bytes.Length == 0) continue;
            if (d.Offset < 0 || d.Offset + 8 > captured.Length) continue;
            nint buf = Marshal.AllocHGlobal(d.Bytes.Length);
            Marshal.Copy(d.Bytes, 0, buf, d.Bytes.Length);
            if (strip)
                for (int j = 0; j + 8 <= d.Bytes.Length; j += 8)
                {
                    ulong w = BitConverter.ToUInt64(d.Bytes, j);
                    if (w >= PtrMin && w <= PtrMax)
                        for (int k = 0; k < 8; k++) Marshal.WriteByte(buf + j + k, 0);
                }
            coldAllocs.Add(buf);
            var addrBytes = BitConverter.GetBytes((ulong)buf);
            Array.Copy(addrBytes, 0, captured, d.Offset, 8);   // point the captured EnvState word at OUR descriptor
            n++;
            sb.Append("\n    0x").Append(d.Offset.ToString("X")).Append(" -> self-alloc 0x").Append(buf.ToString("X"))
              .Append(" (").Append(d.Bytes.Length).Append(" bytes").Append(strip ? ", inner ptrs ZEROED" : "")
              .Append(") path=\"").Append(d.Path).Append('"');
        }
        skipWord = null;   // WHOLESALE so the re-pointed 0x2C0/0x2C8 words are copied verbatim into the live EnvState
        log.Information("[HMSync] [" + tag + "] re-established " + n + " doodad(s) for weather " + capturedWeather
            + (strip ? " (STRIP inner ptrs)" : " (faithful copy)") + ":" + sb);
        return n;
    }

    // b120: expose the doodads snapshotted at the last live capture, as shippable DoodadBake records — the bake pipeline
    // persists these into the preset so a later persisted/synced apply can re-establish them (ReestablishDoodads).
    public System.Collections.Generic.IReadOnlyList<DoodadBake> GetCapturedDoodads()
    {
        var outp = new System.Collections.Generic.List<DoodadBake>();
        if (capturedDoodads != null)
            foreach (var d in capturedDoodads)
                outp.Add(new DoodadBake { Offset = d.WordOffset, Path = d.Path, Bytes = (byte[])d.Bytes.Clone() });
        return outp;
    }

    private void FreeColdAllocs()
    {
        foreach (var p in coldAllocs) { try { Marshal.FreeHGlobal(p); } catch { } }
        coldAllocs.Clear();
    }

    // Read a ResourceHandle's FileName (std::string @+0x48). Fully guarded: the base 0xB0 window is already validated by
    // the caller; the only out-of-window read is the heap buffer of a long (>15 char) string, which we VirtualQuery too.
    private string? TryReadResourceFileName(nint handle)
    {
        try
        {
            nint fn = handle + RhFileNameOffset;
            ulong size = *(ulong*)(fn + 0x10);
            ulong cap = *(ulong*)(fn + 0x18);
            if (size == 0 || size > 512) return null;   // sane bound — real resource paths are well under this
            int len = (int)size;
            var buf = new byte[len];
            if (cap <= 15)
            {
                Marshal.Copy(fn, buf, 0, len);          // SSO: chars inline in the union (inside the validated base window)
            }
            else
            {
                nint sp = *(nint*)fn;                    // heap buffer pointer
                if (!IsReadable(sp, len)) return null;
                Marshal.Copy(sp, buf, 0, len);
            }
            int end = Array.IndexOf(buf, (byte)0);
            if (end < 0) end = len;
            return System.Text.Encoding.UTF8.GetString(buf, 0, end);
        }
        catch { return null; }
    }

    // b117: raw hex+ASCII window at a (VirtualQuery-validated) target pointer. Reads up to `len` bytes but clamps to the
    // committed-readable region so it never faults. Formatted 16 bytes/line: "+0xNN  HH HH .. | ascii". This is the
    // "what does 0x2C0/0x2C8 actually point to" diagnostic — the ResourceHandle layout guess is dead, so look at bytes.
    private string DumpRawAt(nint addr, int len)
    {
        try
        {
            if (!IsReadable(addr, 1)) return "\n      (target unreadable)";
            // Clamp len to what's actually committed+readable from addr, in 0x10 steps, so a short region can't fault.
            int safe = 0;
            while (safe + 0x10 <= len && IsReadable(addr + safe, 0x10)) safe += 0x10;
            if (safe == 0) safe = IsReadable(addr, 0x10) ? 0x10 : 8;
            var buf = new byte[safe];
            Marshal.Copy(addr, buf, 0, safe);
            var sb = new System.Text.StringBuilder();
            for (int row = 0; row < safe; row += 16)
            {
                sb.Append("\n      +0x").Append(row.ToString("X2")).Append("  ");
                int n = Math.Min(16, safe - row);
                for (int j = 0; j < n; j++) sb.Append(buf[row + j].ToString("X2")).Append(' ');
                for (int j = n; j < 16; j++) sb.Append("   ");
                sb.Append("| ");
                for (int j = 0; j < n; j++)
                {
                    byte b = buf[row + j];
                    sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }
            }
            return sb.ToString();
        }
        catch (Exception ex) { return "\n      (dump failed: " + ex.Message + ")"; }
    }

    // b118: read a null-terminated ASCII path directly at a target pointer (the avfx descriptor begins with its
    // resource path inline). VirtualQuery-guarded in 0x10 steps so it never faults; stops at NUL or first non-printable.
    private string? TryReadCStringAt(nint addr, int maxLen)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < maxLen; i += 0x10)
            {
                if (!IsReadable(addr + i, 0x10)) break;
                var chunk = new byte[0x10];
                Marshal.Copy(addr + i, chunk, 0, 0x10);
                foreach (byte b in chunk)
                {
                    if (b == 0) return sb.ToString();
                    if (b < 0x20 || b >= 0x7F) return sb.ToString();   // non-printable → end of string field
                    sb.Append((char)b);
                }
            }
            return sb.ToString();
        }
        catch { return null; }
    }

    private static string CategoryName(ushort c) => c switch
    {
        0 => "Common", 1 => "BgCommon", 2 => "Bg", 3 => "Cut", 4 => "Chara", 5 => "Shader",
        6 => "Ui", 7 => "Sound", 8 => "Vfx", 9 => "UiScript", 10 => "Exd", 11 => "GameScript", 12 => "Music",
        _ => "?"
    };

    // VirtualQuery-gated readability check — the crash guard for probing a pointer-range word that may not be a real
    // handle. True only if [addr, addr+length) lies entirely in one committed, readable, non-guard page region.
    private static bool IsReadable(nint addr, int length)
    {
        if (addr == 0 || length <= 0) return false;
        if (VirtualQuery(addr, out var mbi, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0) return false;
        if (mbi.State != MEM_COMMIT) return false;
        uint p = mbi.Protect;
        if ((p & (PAGE_GUARD | PAGE_NOACCESS)) != 0) return false;
        const uint readable = PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
        if ((p & readable) == 0) return false;
        ulong regionEnd = (ulong)mbi.BaseAddress + (ulong)mbi.RegionSize;
        return (ulong)addr + (ulong)length <= regionEnd;
    }

    // b126 (wxsweep guard): classify a baked doodad set's INNER pointer words by current readability. Each descriptor's
    // bytes carry stale DONOR-session heap addresses; at re-establish the game's env routine (sub_1402FE240) dereferences
    // one and faults if it lands in unmapped memory (the 207 CTD, reading ptr+0x20). We can't PREDICT safety (150 has
    // unmapped-but-harmless pointers too — the game rebuilds before deref), but "any inner pointer unmapped" strongly
    // correlates with the crash, so it's a useful crash-REDUCER for the auto-sweep: skip descriptors with dangling words.
    // Returns (total pointer-range words, how many are currently unmapped). unmapped>0 ⇒ risky (skip in guarded mode).
    public (int total, int unmapped) InnerPointerReadability(System.Collections.Generic.IReadOnlyList<DoodadBake> doods)
    {
        int total = 0, bad = 0;
        if (doods == null) return (0, 0);
        foreach (var d in doods)
        {
            var b = d.Bytes;
            if (b == null) continue;
            for (int j = 0; j + 8 <= b.Length; j += 8)
            {
                ulong w = BitConverter.ToUInt64(b, j);
                if (w < PtrMin || w > PtrMax) continue;
                total++;
                if (!IsReadable((nint)w, 0x20)) bad++;   // 0x20 = the offset the env routine reads (matches the fault)
            }
        }
        return (total, bad);
    }

    // ── b130 wxdooddiag: VEH fault-capture API ────────────────────────────────────────────────────────────────────
    // Arm the Vectored Exception Handler + pre-open the forensic log. The next 207/208 cram will CTD as usual, but the
    // VEH first writes an [WXFAULT] line (walked offset + .avfx path) to `logPath` and flushes it, so after relaunch the
    // discriminator is on disk. `logPath` is the plugin config dir's weather-fault.log (passed by the command handler).
    public string ArmFaultDiag(string logPath)
    {
        if (hook == null) return "[HMSync] wxdooddiag: cram hook unavailable — nothing to diagnose.";
        if (envRoutineLo == 0) return "[HMSync] wxdooddiag: module base unresolved — cannot scope the VEH (see /xllog).";
        if (faultDiagArmed) return "[HMSync] wxdooddiag: already ARMED. Apply weather 207/208 to trip it.";
        faultDiagHits = 0;
        // Pre-open (append) so the VEH only has to Write+Flush — the fault kills the process, and /xllog is not a
        // reliable store across a hard native CTD (wxsweep precedent), so we persist to disk synchronously in-handler.
        try
        {
            faultLogPath = logPath;
            faultLog = new System.IO.StreamWriter(
                new System.IO.FileStream(logPath, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite))
                { AutoFlush = false };
            faultLog.WriteLine("# wxdooddiag armed " + DateTime.Now.ToString("s"));
            faultLog.Flush();
        }
        catch (Exception ex) { faultLog = null; log.Warning("[HMSync] wxdooddiag: could not open fault log '" + logPath + "': " + ex.Message); }
        vehDelegate = FaultHandler;                       // root the delegate for the lifetime of the registration
        vehHandle = AddVectoredExceptionHandler(1, vehDelegate);   // first=1 → runs before the game's own handlers
        faultDiagArmed = vehHandle != 0;
        if (!faultDiagArmed) { vehDelegate = null; return "[HMSync] wxdooddiag: AddVectoredExceptionHandler failed."; }
        log.Information("[HMSync] [WXFAULT] ARMED scoping RIP to sub_1402FE240 window 0x"
            + envRoutineLo.ToString("X") + "..0x" + envRoutineHi.ToString("X") + ", log=" + logPath + ". Now apply weather 207 or 208.");
        return "[HMSync] wxdooddiag: ARMED (CAPTURE-then-crash). Apply weather 207 (Auroral Flares) or 208 (Floracane); "
            + "it WILL crash to desktop — after relaunch read " + logPath + " (or /xllog [WXFAULT]) for the walked offset.";
    }

    public string DisarmFaultDiag()
    {
        if (!faultDiagArmed) return "[HMSync] wxdooddiag: not armed.";
        if (vehHandle != 0) { RemoveVectoredExceptionHandler(vehHandle); vehHandle = 0; }
        vehDelegate = null;
        faultDiagArmed = false;
        try { faultLog?.Flush(); faultLog?.Dispose(); } catch { }
        faultLog = null;
        return "[HMSync] wxdooddiag: DISARMED after " + faultDiagHits + " captured fault(s).";
    }

    public string FaultDiagStatus() =>
        "[HMSync] wxdooddiag: " + (faultDiagArmed ? "ARMED" : "idle")
        + " (hits=" + faultDiagHits + (faultLogPath.Length > 0 ? ", log=" + faultLogPath : "")
        + "). Usage: `wxdooddiag arm` · `wxdooddiag off` · `wxdooddiag status`.";

    // The VEH itself. Runs on the faulting (game) thread, first-chance, for EVERY process exception — so it must be
    // cheap and bail instantly on anything that isn't OUR access violation in the env routine. Reading the EXCEPTION_
    // RECORD / CONTEXT via raw offsets is safe during dispatch. Everything is wrapped so a diag bug can never itself
    // crash the game (returns CONTINUE_SEARCH on any trouble = "not my exception").
    private int FaultHandler(nint info)
    {
        try
        {
            if (info == 0) return VehContinueSearch;
            nint rec = *(nint*)info;               // EXCEPTION_POINTERS.ExceptionRecord
            nint ctx = *(nint*)(info + 8);          // EXCEPTION_POINTERS.ContextRecord
            if (rec == 0 || ctx == 0) return VehContinueSearch;
            uint code = *(uint*)rec;                // EXCEPTION_RECORD.ExceptionCode @+0x00
            if (code != 0xC0000005) return VehContinueSearch;   // only access violations
            nint rip = *(nint*)(ctx + 0xF8);        // CONTEXT.Rip (x64)
            if ((ulong)rip < (ulong)envRoutineLo || (ulong)rip >= (ulong)envRoutineHi) return VehContinueSearch;
            nint rdx = *(nint*)(ctx + 0x88);        // CONTEXT.Rdx
            nint faultAddr = *(nint*)(rec + 0x28);  // EXCEPTION_RECORD.ExceptionInformation[1] = the address read
            faultDiagHits++;
            // The faulting instruction (sub_1402FE240+0x6) reads [RDX] at offset 0 — confirmed by the crash dump
            // (Parameters: 0, <faultAddr>; RDX==RSI==faultAddr). So match the fault address directly (the b130 -0x20
            // was a wrong guess; the rdx arg already covered it, but pass faultAddr straight so this reads true later).
            string trace = MatchFaultToDescriptor(faultAddr, rdx);
            string line = "[WXFAULT] #" + faultDiagHits + " weather " + capturedWeather
                + " rip=+0x" + ((ulong)rip - moduleBase).ToString("X")
                + " rdx=0x" + rdx.ToString("X") + " fault=0x" + faultAddr.ToString("X") + trace;
            // Persist to disk FIRST (flushed) — the process is about to die and /xllog may not survive it. Then best-
            // effort log + stash for chat (chat almost never prints before the crash, but harmless if it does).
            try { faultLog?.WriteLine(line); faultLog?.Flush(); } catch { }
            try { log.Warning("[HMSync] " + line); } catch { }
            pendingFaultChat = "[HMSync] " + line + " — crashing.";
            return VehContinueSearch;                    // let the normal C0000005 crash proceed; the line is on disk
        }
        catch { return VehContinueSearch; }
    }

    // Match a fault's bad pointer (the fault address == the RDX the routine dereferenced at [RDX]) to a word in the last
    // set of re-established descriptors — naming the walked offset + .avfx path if the dangling pointer came straight
    // from our bytes. For 207/208 it does NOT match: the fault is SECOND-ORDER (descriptor → valid intermediate object
    // → dangling pointer INSIDE that object → fault), so the bad address never appears in the descriptor we baked.
    private string MatchFaultToDescriptor(nint badPtr, nint rdx)
    {
        var bakes = lastBakes;
        if (bakes == null || bakes.Count == 0) return " (no re-established descriptors on record)";
        ulong t1 = (ulong)badPtr, t2 = (ulong)rdx;
        foreach (var d in bakes)
        {
            var b = d.Bytes;
            if (b == null) continue;
            for (int j = 0; j + 8 <= b.Length; j += 8)
            {
                ulong w = BitConverter.ToUInt64(b, j);
                if (w == t1 || w == t2)
                    return " → WALKED offset 0x" + j.ToString("X") + " in descriptor (EnvState word 0x"
                        + d.Offset.ToString("X") + ", path=\"" + d.Path + "\") holds the bad pointer 0x" + w.ToString("X");
            }
        }
        return " (bad pointer not among last re-established descriptor words — likely a SECOND-ORDER deref: the routine "
            + "read a valid word, followed it, and faulted one hop deeper)";
    }

    private const int VehContinueSearch = 0;       // EXCEPTION_CONTINUE_SEARCH

    [DllImport("kernel32.dll")]
    private static extern nint AddVectoredExceptionHandler(uint first, VectoredHandler handler);
    [DllImport("kernel32.dll")]
    private static extern uint RemoveVectoredExceptionHandler(nint handle);
    private delegate int VectoredHandler(nint exceptionInfo);

    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01, PAGE_READONLY = 0x02, PAGE_READWRITE = 0x04, PAGE_WRITECOPY = 0x08;
    private const uint PAGE_EXECUTE_READ = 0x20, PAGE_EXECUTE_READWRITE = 0x40, PAGE_EXECUTE_WRITECOPY = 0x80, PAGE_GUARD = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public nint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nint lpAddress, out MemoryBasicInformation lpBuffer, nuint dwLength);

    // Precompute, once per captured block, which 8-byte words look like canonical x64 user-space heap pointers
    // ([PtrMin, PtrMax]) — those are the resource handles we must NOT restamp. Logs the preserved offsets once so the
    // exact pointer-field map can be pinned and hardcoded later.
    private void BuildSkipMask(byte[] block)
    {
        int words = block.Length / 8;
        var mask = new bool[words];
        var preserved = new System.Text.StringBuilder();
        int n = 0;
        for (int i = 0; i < words; i++)
        {
            ulong v = BitConverter.ToUInt64(block, i * 8);
            if (v >= PtrMin && v <= PtrMax)
            {
                mask[i] = true;
                n++;
                if (preserved.Length > 0) preserved.Append(", ");
                preserved.Append("0x").Append((i * 8).ToString("X")).Append("=0x").Append(v.ToString("X"));
            }
        }
        skipWord = mask;
        log.Information("[HMSync] [WXCRAM] skip-mask: preserving " + n + " pointer-range word(s) of " + words
            + " [" + preserved + "]");
    }

    // b177: UNION skip-mask across an ENTIRE keyframe set — the correct mask for the day-night graft. BuildSkipMask reads
    // ONE block (keyframe[0]); a handle word that is null at that instant but POPULATED in a later keyframe escapes it, and
    // RestampInterpolated then blends 0→pointer→0 across the midday bracket and stamps a live EnvState handle with garbage →
    // the game dereferences it → AV. This is exactly the b175/b176 "only Clear Skies · Tuliyollal crashes" bug: that set is
    // the lone one whose 0x2C8 handle was captured intermittently (kf 12–19 only, null in kf 0); every other set has its
    // 0x2C0/0x2C8/0x2D0 handles populated in ALL frames, so keyframe[0] happened to catch them. Fix: preserve a word if it
    // looks like a pointer in ANY keyframe of the set. Zero visual cost — those offsets are resource handles, never sky floats.
    private void BuildSkipMaskUnion(System.Collections.Generic.IReadOnlyList<(float tod, byte[] block)> kfs)
    {
        if (kfs == null || kfs.Count == 0) { skipWord = null; return; }
        int words = kfs[0].block.Length / 8;
        var mask = new bool[words];
        foreach (var (_, block) in kfs)
        {
            if (block == null || block.Length < words * 8) continue;
            for (int i = 0; i < words; i++)
            {
                if (mask[i]) continue;
                ulong v = BitConverter.ToUInt64(block, i * 8);
                if (v >= PtrMin && v <= PtrMax) mask[i] = true;
            }
        }
        int n = 0; var preserved = new System.Text.StringBuilder();
        for (int i = 0; i < words; i++)
            if (mask[i]) { n++; if (preserved.Length > 0) preserved.Append(", "); preserved.Append("0x").Append((i * 8).ToString("X")); }
        skipWord = mask;
        log.Information("[HMSync] [WXCRAM] skip-mask(union): preserving " + n + " pointer-range word(s) of " + words
            + " across " + kfs.Count + " keyframes [" + preserved + "]");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    // PATH I DIAGNOSTIC (b137, wxenvprobe) — map the in-memory injection surface for donor-BANK injection.
    //
    // WHY (WEATHER-CRAM-MECHANISM §7 / Handbook §11.8): the EnvState restamp (this service's b96 mechanism) copies the
    // donor's LOOK at one instant → a crammed sky is frozen (no day/night, and the avfx are re-established by hand).
    // Path I instead makes the game's OWN UpdateEnvironment sample the DONOR's keyframe set by EorzeaTime — recovering
    // cycling + native avfx for free. The open question was WHERE the parsed keyframe set lives and whether it's
    // reachable without a hardware-watchpoint resolver hunt. It IS: the CS structs map the whole path —
    //   EnvManager.Instance() -> EnvScene (+0x08) -> _envSpaces[8] (EnvScene+0xF0, inline EnvSpace structs, 0xF0 each)
    //   -> EnvSpace.EnvSetResourceHandle (+0x90) -> EnvSetResourceHandle (an .envb ResourceHandle; base 0xB0, ext to 0xC8).
    // The base ResourceHandle carries FileName@+0x48 (the loaded .envb path) and FileSize@+0x28; the 0x18 bytes past the
    // base handle (0xB0..0xC8) are the EnvSet-specific parse (candidate: pointer to the parsed set array + count). This
    // probe reads all of that (VirtualQuery-gated, ZERO writes) so the exact injection point can be designed from
    // measured layout, not guessed. Confirms: which envb the zone loaded, its WeatherIds table, and whether the parsed
    // ENVB blob is reachable in memory from the handle.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    private const int EnvSceneOffset = 0x08;          // EnvManager -> EnvScene*
    private const int WeatherIdsOffset = 0x30;         // EnvScene -> FixedSizeArray32<byte> _weatherIds
    private const int EnvSpacesOffset = 0xF0;          // EnvScene -> FixedSizeArray8<EnvSpace> _envSpaces (inline structs)
    private const int EnvSpaceSize = 0xF0;             // sizeof(EnvSpace)
    private const int EnvSpaceCount = 8;
    private const int EnvSetHandleOffset = 0x90;       // EnvSpace -> EnvSetResourceHandle*
    private const int RhFileSizeOffset = 0x28;         // ResourceHandle.FileSize
    private const int RhBaseSize = 0xB0;               // base ResourceHandle size; EnvSet ext runs 0xB0..0xC8

    public string ProbeEnvSet()
    {
        try
        {
            var env = EnvManager.Instance();
            if (env == null) return "[HMSync] wxenvprobe: EnvManager null — in a zone?";
            nint scene = (nint)(*(nint*)((nint)env + EnvSceneOffset));
            float dt = *(float*)((nint)env + 0x10);
            byte active = env->ActiveWeather;
            var sb = new System.Text.StringBuilder();
            sb.Append("[HMSync] [WXENV] EnvMgr=0x").Append(((nint)env).ToString("X"))
              .Append(" EnvScene=0x").Append(scene.ToString("X"))
              .Append(" ActiveWeather=").Append(active)
              .Append(" DayTime=").Append(dt.ToString("0")).Append("s");

            if (scene == 0 || !IsReadable(scene, 0x890))
            {
                log.Warning(sb.Append(" — EnvScene unreadable").ToString());
                return "[HMSync] wxenvprobe: EnvScene pointer null/unreadable (see /xllog).";
            }

            // WeatherIds[32] — the resolve table (§11.4). A crammed foreign id absent here misses to slot 0.
            var wids = new byte[32];
            Marshal.Copy(scene + WeatherIdsOffset, wids, 0, 32);
            sb.Append("\n  WeatherIds[32]: ");
            for (int i = 0; i < 32; i++) { if (i > 0) sb.Append(','); sb.Append(wids[i]); }

            int spacesLive = 0;
            for (int s = 0; s < EnvSpaceCount; s++)
            {
                nint spaceBase = scene + EnvSpacesOffset + s * EnvSpaceSize;
                if (!IsReadable(spaceBase + EnvSetHandleOffset, 8)) continue;
                nint handle = *(nint*)(spaceBase + EnvSetHandleOffset);
                if (handle == 0) continue;
                spacesLive++;
                sb.Append("\n  EnvSpace[").Append(s).Append("] @0x").Append(spaceBase.ToString("X"))
                  .Append("  EnvSetHandle=0x").Append(handle.ToString("X"));
                if (!IsReadable(handle, 0xC8))
                {
                    sb.Append("  <handle unreadable>");
                    continue;
                }
                uint fileSize = (uint)Marshal.ReadInt32(handle + RhFileSizeOffset);
                string path = TryReadResourceFileName(handle) ?? "<no name>";
                sb.Append("  file=\"").Append(path).Append("\" size=").Append(fileSize);
                // The EnvSet-specific parse: 3 qwords past the base ResourceHandle (0xB0/0xB8/0xC0). Any that is a
                // readable pointer gets a 0x40 window so we can spot the 'ENVB' magic or the parsed set table.
                for (int q = 0; q < 3; q++)
                {
                    int off = RhBaseSize + q * 8;
                    ulong w = (ulong)Marshal.ReadInt64(handle + off);
                    sb.Append("\n      ext+0x").Append(off.ToString("X")).Append(" = 0x").Append(w.ToString("X"));
                    if (w >= PtrMin && w <= PtrMax && IsReadable((nint)w, 0x10))
                    {
                        // Is it the raw ENVB blob, or a table that points at it?
                        var head = new byte[4];
                        Marshal.Copy((nint)w, head, 0, 4);
                        bool isEnvb = head[0] == (byte)'E' && head[1] == (byte)'N' && head[2] == (byte)'V' && head[3] == (byte)'B';
                        sb.Append(isEnvb ? "  <-- ENVB blob!" : "  (ptr)").Append(DumpRawAt((nint)w, 0x40));
                        // b138: the ext+0xC0 word (Kugane/Lapis) is a PARSED EnvSet object — first qword is a vtable in
                        // the exe's code range (>= moduleBase). Its +0x10/+0x18 are sub-array pointers and +0x20(u16) is
                        // the set count. Follow those sub-arrays (read-only) so we can tell whether the parsed object
                        // carries its OWN per-slot weatherId list (→ a handle swap is self-contained) or relies on
                        // EnvScene.WeatherIds (→ a swap needs a parallel WeatherIds rewrite for index alignment). This is
                        // the fact that picks the Path I injection form.
                        if (!isEnvb) DumpParsedEnvSet((nint)w, sb);
                    }
                }
            }
            log.Information(sb.ToString());
            return "[HMSync] wxenvprobe: EnvScene=0x" + scene.ToString("X") + ", " + spacesLive + " live EnvSpace(s), "
                + "ActiveWeather=" + active + ". Full layout (loaded .envb path, WeatherIds table, EnvSet parse words) "
                + "in /xllog [WXENV] — Path I injection surface.";
        }
        catch (Exception ex) { log.Error("[HMSync] wxenvprobe failed: " + ex.Message); return "[HMSync] wxenvprobe failed: " + ex.Message; }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    // b148 (Path I, starfield/skydome gap) — wxskydiag: read-only dump of the SKY + AMBIENT resource surface.
    //
    // WHY: the cycle cram swaps EnvSpace.EnvSetResourceHandle (+0x90 = the .envb env-PARAMS: sun angle, fog, colours).
    // But the night STARFIELD and the map's AMBIENT fill light are NOT in those params — they hang off the per-EnvSpace
    // EnvLocation (EnvSpace+0xB0): AmbientSetResource(+0x90), EnvironmentCubemapResource(+0x98 → the skybox .tex that
    // carries the star texture) and the resolved Texture*/AmbientSet at +0xA8/+0xA0. Plus a scene-wide CubemapArray
    // (EnvScene+0x8D0). The cram leaves the EnvLocation untouched, so a fixed-time DUNGEON keeps ITS OWN sky cubemap
    // (a cave / black night, no stars) and its own ambient — exactly the b147 report: "the sun moved, but it robbed the
    // map of all light and the sky is black regardless of time." This probe dumps those resource PATHS for the current
    // zone so a real city's sky/ambient resources can be diffed against the dungeon's, deciding whether Path I must ALSO
    // cram the cubemap+ambient (a swappable resource) or fall back to bake-and-replay. ZERO writes; VirtualQuery-gated.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    private const int EnvSpaceLocationOffset = 0xB0;   // EnvSpace -> EnvLocation*
    private const int ElAmbientResOffset = 0x90;       // EnvLocation -> AmbientSetResourceHandle*
    private const int ElCubemapResOffset = 0x98;       // EnvLocation -> TextureResourceHandle* (skybox .tex → starfield)
    private const int ElAmbientSetOffset = 0xA0;       // EnvLocation -> AmbientSet (void*, resolved)
    private const int ElCubemapTexOffset = 0xA8;       // EnvLocation -> Texture* (resolved skybox)
    private const int SceneLocationsOffset = 0x880;    // EnvScene -> EnvLocation** Locations (up to 32)
    private const int SceneLocationCountOffset = 0x888;
    private const int SceneCubemapArrayOffset = 0x8D0; // EnvScene -> Texture* CubemapArray

    public string SkyDiag()
    {
        try
        {
            var env = EnvManager.Instance();
            if (env == null) return "[HMSync] wxskydiag: EnvManager null — in a zone?";
            nint scene = (nint)(*(nint*)((nint)env + EnvSceneOffset));
            if (scene == 0 || !IsReadable(scene, 0x8E0)) return "[HMSync] wxskydiag: EnvScene null/unreadable.";

            var sb = new System.Text.StringBuilder();
            sb.Append("[HMSync] [WXSKY] EnvScene=0x").Append(scene.ToString("X"))
              .Append(" ActiveWeather=").Append(env->ActiveWeather)
              .Append(cycleActive ? "  (CRAM ACTIVE donor=\"" + cycleDonorPath + "\")" : "  (no cram)");

            // Scene-wide sky texture array + the Locations table.
            nint cubeArr = (nint)Marshal.ReadInt64(scene + SceneCubemapArrayOffset);
            uint locCount = (uint)Marshal.ReadInt32(scene + SceneLocationCountOffset);
            nint locs = (nint)Marshal.ReadInt64(scene + SceneLocationsOffset);
            sb.Append("\n  CubemapArray(Texture*)=0x").Append(cubeArr.ToString("X"))
              .Append("  Locations=0x").Append(locs.ToString("X")).Append(" count=").Append(locCount);

            // Per-EnvSpace EnvLocation — the actual sky/ambient owner (NOT swapped by the +0x90 env-param cram).
            for (int s = 0; s < EnvSpaceCount; s++)
            {
                nint spaceBase = scene + EnvSpacesOffset + s * EnvSpaceSize;
                if (!IsReadable(spaceBase + EnvSetHandleOffset, 8)) continue;
                nint handle = *(nint*)(spaceBase + EnvSetHandleOffset);
                nint loc = IsReadable(spaceBase + EnvSpaceLocationOffset, 8) ? *(nint*)(spaceBase + EnvSpaceLocationOffset) : 0;
                if (handle == 0 && loc == 0) continue;
                sb.Append("\n  EnvSpace[").Append(s).Append("] EnvLocation=0x").Append(loc.ToString("X"));
                if (loc == 0 || !IsReadable(loc, 0xB0)) { sb.Append(loc == 0 ? "  (none)" : "  (unreadable)"); continue; }
                nint ambRes = (nint)Marshal.ReadInt64(loc + ElAmbientResOffset);
                nint cubeRes = (nint)Marshal.ReadInt64(loc + ElCubemapResOffset);
                nint ambSet = (nint)Marshal.ReadInt64(loc + ElAmbientSetOffset);
                nint cubeTex = (nint)Marshal.ReadInt64(loc + ElCubemapTexOffset);
                string ambPath = (ambRes != 0 && IsReadable(ambRes, 0xC8)) ? (TryReadResourceFileName(ambRes) ?? "<no name>") : "<null>";
                string cubePath = (cubeRes != 0 && IsReadable(cubeRes, 0xC8)) ? (TryReadResourceFileName(cubeRes) ?? "<no name>") : "<null>";
                sb.Append("\n      AmbientSetResource=0x").Append(ambRes.ToString("X")).Append(" \"").Append(ambPath).Append('"')
                  .Append("  AmbientSet=0x").Append(ambSet.ToString("X"))
                  .Append("\n      CubemapResource=0x").Append(cubeRes.ToString("X")).Append(" \"").Append(cubePath).Append('"')
                  .Append("  Cubemap(Texture*)=0x").Append(cubeTex.ToString("X"));
            }

            log.Information(sb.ToString());
            return "[HMSync] wxskydiag: sky+ambient resource surface for this zone in /xllog [WXSKY] — "
                + "AmbientSetResource + EnvironmentCubemapResource paths per EnvSpace. Run in a real city AND in the "
                + "crammed dungeon to diff which sky/ambient resources the +0x90 env-param swap leaves behind.";
        }
        catch (Exception ex) { log.Error("[HMSync] wxskydiag failed: " + ex); return "[HMSync] wxskydiag failed: " + ex.Message; }
    }

    // ── b154 (Path I: is the STAR pass even running?) ────────────────────────────────────────────────────────────
    // b152/b153 swapped EVERY per-zone sky resource (env params + cubemap + ambient, all RESOLVED) and night is STILL
    // black/starless — so either the dedicated StarRenderer isn't drawing for this scene (scene-gated), or it draws and
    // is occluded by the CloudRenderer (operator's theory). Both are GLOBAL renderers hung off the Render::Manager
    // singleton (StarRenderer@0x36570, CloudRenderer@0x36640, both BaseRenderer subclasses). Their interiors are unmapped
    // in CS, so this dumps each as filtered floats + a byte/flag window + vtable, plus Manager.Is3DRenderingDisabled.
    // READ-ONLY. Run in REAL Kugane at night (stars visible) AND in the crammed dungeon (no stars) and DIFF: if the
    // StarRenderer state differs (a live-in-Kugane field reads zero/off in the dungeon) → scene-gated, chase that field;
    // if StarRenderer looks identical → it IS drawing and the clouds occlude → chase CloudRenderer. Offsets from CS Render/Manager.cs.
    private const int RmStarRendererOffset  = 0x36570;
    private const int RmCloudRendererOffset = 0x36640;
    private const int RmIs3DDisabledOffset  = 0x38358;
    public string StarDiag()
    {
        try
        {
            var mgr = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager.Instance();
            if (mgr == null) return "[HMSync] wxstardiag: Render::Manager null.";
            nint mb = (nint)mgr;
            var env = EnvManager.Instance();
            byte wx = env != null ? env->ActiveWeather : (byte)0;

            var sb = new System.Text.StringBuilder();
            sb.Append("[HMSync] [WXSTAR] Render::Manager=0x").Append(mb.ToString("X"))
              .Append("  ActiveWeather=").Append(wx)
              .Append("  Is3DRenderingDisabled=").Append(IsReadable(mb + RmIs3DDisabledOffset, 1) ? Marshal.ReadByte(mb + RmIs3DDisabledOffset).ToString() : "?")
              .Append(cycleActive ? "  (CRAM ACTIVE)" : "  (no cram)");

            DumpRenderer(sb, "StarRenderer",  mb + RmStarRendererOffset,  0xD0);
            DumpRenderer(sb, "CloudRenderer", mb + RmCloudRendererOffset, 0x110); // stop before ShadowCamera@0x110 (matrix noise)

            log.Information(sb.ToString());
            return "[HMSync] wxstardiag: StarRenderer + CloudRenderer state in /xllog [WXSTAR]. Run in REAL Kugane at NIGHT "
                + "(stars visible) AND in the crammed dungeon (no stars) and diff — a field that's live in Kugane but "
                + "zero/off in the dungeon fingerprints the star gate; identical state means the clouds occlude.";
        }
        catch (Exception ex) { log.Error("[HMSync] wxstardiag failed: " + ex); return "[HMSync] wxstardiag failed: " + ex.Message; }
    }

    // Dump a global renderer: vtable + in-module check, then every 4-byte slot in [0x08, size) that reads as a plausible
    // float (finite, |v|<1e6, nonzero) OR a small nonzero int/flag — the "alive" fields. Change-gated diffing is done by
    // the operator across the Kugane/dungeon A/B. Fully VirtualQuery-gated; ZERO writes.
    private void DumpRenderer(System.Text.StringBuilder sb, string name, nint b, int size)
    {
        sb.Append("\n  ").Append(name).Append("=0x").Append(b.ToString("X"));
        if (!IsReadable(b, size)) { sb.Append("  (unreadable)"); return; }
        ulong vt = (ulong)Marshal.ReadInt64(b);
        bool vtInModule = moduleBase != 0 && vt >= moduleBase && vt < moduleBase + 0x08000000UL;
        sb.Append("  vtable=0x").Append(vt.ToString("X")).Append(vtInModule ? " (in-module ✓)" : " (?)");
        var floats = new System.Text.StringBuilder();
        var ints = new System.Text.StringBuilder();
        for (int off = 0x08; off < size; off += 4)
        {
            int raw = Marshal.ReadInt32(b + off);
            if (raw == 0) continue;
            float f = BitConverter.Int32BitsToSingle(raw);
            if (!float.IsNaN(f) && !float.IsInfinity(f) && Math.Abs(f) is > 1e-4f and < 1e6f)
                floats.Append(" +0x").Append(off.ToString("X")).Append('=').Append(f.ToString("0.###"));
            else if (raw > 0 && raw < 0x10000)   // small positive int → likely a flag/count/index
                ints.Append(" +0x").Append(off.ToString("X")).Append('=').Append(raw);
        }
        if (floats.Length > 0) sb.Append("\n      floats:").Append(floats);
        if (ints.Length > 0)   sb.Append("\n      ints/flags:").Append(ints);
        if (floats.Length == 0 && ints.Length == 0) sb.Append("\n      (no live float/flag fields in range)");
    }

    // ── b158 (Path I: find the star-intensity SOURCE, don't fight the output) ─────────────────────────────────────
    // b155 proved the native star routine rewrites StarRenderer+0xB0..0xBC EVERY frame (7842 reverts/22s) back toward 0
    // in the crammed dungeon, while it fades them UP to night values in real Kugane. So the intensity is an OUTPUT
    // computed each frame from some INPUT — and the envb cram does NOT feed that input (else the crammed Kugane envb
    // would compute Kugane's night values and there'd be no revert-war). PRIME SUSPECT for the input: the resolved
    // EnvState (EnvMgr+0x58, 0x2F8) — the sky block the renderers sample, which we ALREADY drive via the
    // UpdateEnvironment hook (detourPost). This dumps EnvState as filtered floats + the StarRenderer intensity block as
    // the correlation ANCHOR. READ-ONLY, VirtualQuery-gated. The A/B: run in REAL Kugane at NIGHT and in the crammed
    // dungeon at NIGHT (wxcity active) and DIFF — an EnvState offset that is nonzero in Kugane but zero/absent in the
    // cram (same weather, same time) is the star source; since we own detourPost we can then WRITE it there (native
    // input, no war, sync-deterministic). If NOTHING star-relevant differs in EnvState → the gate is upstream (a
    // territory "sky visible" flag), and we escalate to hooking the star update itself. EnvState also carries the CLOUD
    // params, so the same dump serves the persistent-cloud problem.
    public string StarSrc()
    {
        try
        {
            var env = EnvManager.Instance();
            if (env == null) return "[HMSync] wxstarsrc: EnvManager null — in a zone?";
            nint es = (nint)env + EnvStateOffset;
            var mgr = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager.Instance();
            nint sr = mgr != null ? (nint)mgr + RmStarRendererOffset : 0;

            var sb = new System.Text.StringBuilder();
            sb.Append("[HMSync] [WXSRC] EnvState=0x").Append(es.ToString("X"))
              .Append("  ActiveWeather=").Append(env->ActiveWeather)
              .Append(cycleActive ? "  (CRAM ACTIVE)" : "  (no cram)");
            DumpFloatBlock(sb, "EnvState", es, EnvStateSize, 0x00);
            if (sr != 0) DumpFloatBlock(sb, "StarRenderer.anchor", sr + 0xA8, 0x28, 0xA8); // +0xA8..0xD0 intensity block
            log.Information(sb.ToString());
            return "[HMSync] wxstarsrc: EnvState + StarRenderer anchor in /xllog [WXSRC]. Run in REAL Kugane at NIGHT AND "
                + "in the crammed dungeon at NIGHT (wxcity), paste both — a nonzero-in-Kugane / zero-in-cram EnvState "
                + "float that tracks the star block is the source we can drive via the env hook (no per-frame war).";
        }
        catch (Exception ex) { log.Error("[HMSync] wxstarsrc failed: " + ex); return "[HMSync] wxstarsrc failed: " + ex.Message; }
    }

    // Dump a raw memory block as filtered floats (finite, nonzero, 1e-4<|v|<1e6), labelling each with (offset+labelBase)
    // so a sub-window reports its absolute struct offset. No vtable read (unlike DumpRenderer). Fully VirtualQuery-gated.
    private void DumpFloatBlock(System.Text.StringBuilder sb, string name, nint b, int size, int labelBase)
    {
        sb.Append("\n  ").Append(name).Append("=0x").Append(b.ToString("X"));
        if (!IsReadable(b, size)) { sb.Append("  (unreadable)"); return; }
        var floats = new System.Text.StringBuilder();
        for (int off = 0; off < size; off += 4)
        {
            int raw = Marshal.ReadInt32(b + off);
            if (raw == 0) continue;
            float f = BitConverter.Int32BitsToSingle(raw);
            if (!float.IsNaN(f) && !float.IsInfinity(f) && Math.Abs(f) is > 1e-4f and < 1e6f)
                floats.Append(" +0x").Append((off + labelBase).ToString("X")).Append('=').Append(f.ToString("0.###"));
        }
        sb.Append(floats.Length > 0 ? "\n      floats:" + floats.ToString() : "\n      (no live floats in range)");
    }

    // b155: write Kugane's night star-intensity params into the global StarRenderer so a dungeon (whose per-zone star
    // routine never runs → the block stays 0 → invisible stars) draws stars. Held each frame by TickStarForce. Proof-lever:
    // static night values (no time-fade yet). Independent of the cram/swaps — but only meaningful once the sky is dark.
    public string StartStarForce()
    {
        try
        {
            if (starForceActive) return "[HMSync] wxstarforce: already active. `wxstarforce off` first.";
            var mgr = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager.Instance();
            if (mgr == null) return "[HMSync] wxstarforce: Render::Manager null.";
            nint sr = (nint)mgr + RmStarRendererOffset;
            if (!IsReadable(sr, 0xD0)) return "[HMSync] wxstarforce: StarRenderer unreadable.";

            starRendererAddr = sr;
            starForceOrig = new float[StarForceVals.Length];
            var sb = new System.Text.StringBuilder("[HMSync] [WXSTAR] FORCE StarRenderer=0x").Append(sr.ToString("X")).Append(" —");
            for (int i = 0; i < StarForceVals.Length; i++)
            {
                var (off, val) = StarForceVals[i];
                starForceOrig[i] = *(float*)(sr + off);        // save for restore
                *(float*)(sr + off) = val;                     // force Kugane-night value
                sb.Append(" +0x").Append(off.ToString("X")).Append(':').Append(starForceOrig[i].ToString("0.###")).Append("→").Append(val.ToString("0.###"));
            }
            starForceActive = true;
            starForceRevertCount = 0;
            log.Information(sb.ToString());
            return "[HMSync] wxstarforce: forced Kugane-night star params (+0xB0..0xBC intensity block). If stars now render, "
                + "the star gate is these fields. `wxstarforce off` restores. (Static values — no time-fade yet.)";
        }
        catch (Exception ex) { log.Error("[HMSync] wxstarforce failed: " + ex); return "[HMSync] wxstarforce failed: " + ex.Message; }
    }

    // Per-frame re-assert: hold the forced star params against the zone/env routine (cheap: 6 floats, write only on drift).
    public void TickStarForce()
    {
        if (!starForceActive) return;
        try
        {
            if (starRendererAddr == 0 || !IsReadable(starRendererAddr, 0xD0)) return;
            foreach (var (off, val) in StarForceVals)
            {
                if (*(float*)(starRendererAddr + off) != val) { *(float*)(starRendererAddr + off) = val; starForceRevertCount++; }
            }
        }
        catch { /* never throw on the framework thread */ }
    }

    public string StopStarForce()
    {
        if (!starForceActive) return "[HMSync] wxstarforce: not active.";
        int reverts = starForceRevertCount;
        try
        {
            if (starRendererAddr != 0 && IsReadable(starRendererAddr, 0xD0) && starForceOrig.Length == StarForceVals.Length)
                for (int i = 0; i < StarForceVals.Length; i++)
                    *(float*)(starRendererAddr + StarForceVals[i].off) = starForceOrig[i];
            log.Information("[HMSync] [WXSTAR] FORCE restored star params. reverts=" + reverts);
        }
        catch (Exception ex) { log.Error("[HMSync] wxstarforce restore failed: " + ex); }
        finally { starForceActive = false; starRendererAddr = 0; starForceRevertCount = 0; }
        return "[HMSync] wxstarforce: restored native star params. reverts during force: " + reverts + ".";
    }

    // b138 (Path I): decode one level of the parsed EnvSet object at `parsed` (the ext+0xC0 target). Reads the header
    // fields, then follows the two sub-array pointers at +0x10/+0x18 (dump 0x60 each) so their contents can be matched
    // against the set count (+0x20) and the zone's WeatherIds. Fully VirtualQuery-gated; ZERO writes.
    private void DumpParsedEnvSet(nint parsed, System.Text.StringBuilder sb)
    {
        try
        {
            if (!IsReadable(parsed, 0x28)) { sb.Append("\n        (parsed obj unreadable)"); return; }
            ulong vt = (ulong)Marshal.ReadInt64(parsed + 0x00);
            uint f08 = (uint)Marshal.ReadInt32(parsed + 0x08);
            uint f0C = (uint)Marshal.ReadInt32(parsed + 0x0C);
            nint a = (nint)Marshal.ReadInt64(parsed + 0x10);
            nint b = (nint)Marshal.ReadInt64(parsed + 0x18);
            ushort setCount = (ushort)Marshal.ReadInt16(parsed + 0x20);
            ushort f22 = (ushort)Marshal.ReadInt16(parsed + 0x22);
            uint f24 = (uint)Marshal.ReadInt32(parsed + 0x24);
            bool vtInModule = moduleBase != 0 && vt >= moduleBase && vt < moduleBase + 0x08000000UL;
            sb.Append("\n        parsed EnvSet: vtable=0x").Append(vt.ToString("X")).Append(vtInModule ? " (in-module ✓)" : " (?)")
              .Append("  f08=").Append(f08).Append(" f0C=").Append(f0C)
              .Append("  setCount=").Append(setCount).Append(" f22=").Append(f22).Append(" f24=").Append(f24)
              .Append("\n        arrayA @+0x10 = 0x").Append(a.ToString("X"));
            if ((ulong)a >= PtrMin && IsReadable(a, 0x10)) sb.Append(DumpRawAt(a, 0x60));
            sb.Append("\n        arrayB @+0x18 = 0x").Append(b.ToString("X"));
            if ((ulong)b >= PtrMin && IsReadable(b, 0x10)) sb.Append(DumpRawAt(b, 0x60));
        }
        catch (Exception ex) { sb.Append("\n        (parsed decode failed: ").Append(ex.Message).Append(')'); }
    }

    // Handle-arg offsets on the base ResourceHandle (verified _GitHub\FFXIVClientStructs-main\...\Handle\ResourceHandle.cs):
    //   +0x08 Type (ResourceHandleType = category|expansion, 4 bytes) — GetResourceSync arg1 (really ResourceHandleType*)
    //   +0x0C FileType (the "envb" FourCC from the file header)         — GetResourceSync arg2 (uint* type)
    //   +0x10 Id       (the sqpack path hash)                           — GetResourceSync arg3 (uint* hash)
    //   +0x48 FileName (StdString, full loaded game path)               — GetResourceSync arg4 (path)
    private const int RhTypeOffset = 0x08;
    private const int RhFileTypeOffset = 0x0C;
    private const int RhIdOffset = 0x10;

    // ── b139 (Path I, native loader) ─────────────────────────────────────────────────────────────────────────────
    // Prove ResourceManager.GetResourceSync can load an .envb into a parsed handle (the +0xC0 vtable'd EnvSet graph the
    // game builds), WITHOUT guessing the native-call args. Every arg is read straight off the CURRENT zone's own live
    // EnvSetResourceHandle: Type(+0x08), FileType(+0x0C = 'envb' FourCC), Id(+0x10 = path hash), FileName(+0x48 = path).
    //   • donorPath == null → SELF-RELOAD the current zone's own FileName. Correct args by construction → the call must
    //     return the SAME cached handle (refcount++), which we then DecRef to balance. This is the zero-risk proof that
    //     our GetResourceSync invocation form is right before we ever vary the path.
    //   • donorPath != null → load a FOREIGN .envb (e.g. Kugane's genv_e3t1.envb). We reuse the live handle's Type +
    //     FileType (same category/FourCC for any .envb) and compute the donor's hash with the crc32 we VALIDATED this
    //     same call against the live Id. On success we dump the donor handle's parsed +0xC0 EnvSet (setCount + weatherId
    //     list) to confirm the graph built, then DecRef (verify only — the actual EnvSpace+0x90 swap is a later build).
    // Writes NOTHING to game structs; GetResourceSync/DecRef are the game's own refcounted resource path. VirtualQuery-
    // gated reads throughout.
    public string LoadVerify(string? donorPath)
    {
        try
        {
            var env = EnvManager.Instance();
            if (env == null) return "[HMSync] wxloadverify: EnvManager null — in a zone?";
            nint scene = (nint)(*(nint*)((nint)env + EnvSceneOffset));
            if (scene == 0 || !IsReadable(scene, 0x890)) return "[HMSync] wxloadverify: EnvScene null/unreadable.";

            // Find the first live EnvSpace's .envb handle to source the call args from.
            nint live = 0;
            for (int s = 0; s < EnvSpaceCount; s++)
            {
                nint spaceBase = scene + EnvSpacesOffset + s * EnvSpaceSize;
                if (!IsReadable(spaceBase + EnvSetHandleOffset, 8)) continue;
                nint h = *(nint*)(spaceBase + EnvSetHandleOffset);
                if (h != 0 && IsReadable(h, 0xC8)) { live = h; break; }
            }
            if (live == 0) return "[HMSync] wxloadverify: no live EnvSetResourceHandle found.";

            uint liveType = (uint)Marshal.ReadInt32(live + RhTypeOffset);
            uint fileType = (uint)Marshal.ReadInt32(live + RhFileTypeOffset);
            uint liveId = (uint)Marshal.ReadInt32(live + RhIdOffset);
            string livePath = TryReadResourceFileName(live) ?? "";

            var sb = new System.Text.StringBuilder();
            sb.Append("[HMSync] [WXLOAD] live handle=0x").Append(live.ToString("X"))
              .Append(" Type=0x").Append(liveType.ToString("X8"))
              .Append(" FileType=0x").Append(fileType.ToString("X8"))
              .Append(" (\"").Append(FourCcToStr(fileType)).Append("\")")
              .Append(" Id=0x").Append(liveId.ToString("X8"))
              .Append("\n  livePath=\"").Append(livePath).Append('"');

            // Validate our crc32 against the live Id so the donor-path hash is proven, not guessed. Try both the
            // raw and lowercased path, and both the non-complemented (sqpack/JAMCRC) and complemented forms.
            string lower = livePath.ToLowerInvariant();
            uint crcRaw = SqPackCrc(System.Text.Encoding.UTF8.GetBytes(livePath), false);
            uint crcRawC = SqPackCrc(System.Text.Encoding.UTF8.GetBytes(livePath), true);
            uint crcLo = SqPackCrc(System.Text.Encoding.UTF8.GetBytes(lower), false);
            uint crcLoC = SqPackCrc(System.Text.Encoding.UTF8.GetBytes(lower), true);
            int mode = crcLo == liveId ? 0 : crcLoC == liveId ? 1 : crcRaw == liveId ? 2 : crcRawC == liveId ? 3 : -1;
            sb.Append("\n  crc[lower/nc]=0x").Append(crcLo.ToString("X8"))
              .Append(" crc[lower/c]=0x").Append(crcLoC.ToString("X8"))
              .Append(" crc[raw/nc]=0x").Append(crcRaw.ToString("X8"))
              .Append(" crc[raw/c]=0x").Append(crcRawC.ToString("X8"))
              .Append("  => Id match: ").Append(mode switch { 0 => "lower/non-complemented", 1 => "lower/complemented", 2 => "raw/non-complemented", 3 => "raw/complemented", _ => "NONE (hash format differs!)" });

            // Decide the load target + hash.
            bool selfReload = string.IsNullOrWhiteSpace(donorPath);
            string targetPath = selfReload ? livePath : donorPath!.Trim();
            uint targetHash;
            if (selfReload)
            {
                targetHash = liveId; // exact, read off the live handle — cache-hit proof.
            }
            else if (mode >= 0)
            {
                string tp = (mode == 0 || mode == 1) ? targetPath.ToLowerInvariant() : targetPath;
                bool comp = (mode == 1 || mode == 3);
                targetHash = SqPackCrc(System.Text.Encoding.UTF8.GetBytes(tp), comp);
            }
            else
            {
                log.Warning(sb.ToString());
                return "[HMSync] wxloadverify: could not validate crc against live Id — refusing to load a foreign path with an unproven hash. See /xllog [WXLOAD].";
            }
            sb.Append("\n  LOAD target=\"").Append(targetPath).Append("\" hash=0x").Append(targetHash.ToString("X8"))
              .Append(selfReload ? " (SELF-RELOAD)" : " (DONOR)");

            var rm = ResourceManager.Instance();
            if (rm == null) { log.Warning(sb.ToString()); return "[HMSync] wxloadverify: ResourceManager null."; }

            uint catRaw = liveType;
            uint typeRaw = fileType;
            uint hashRaw = targetHash;
            ResourceHandle* got;
            fixed (byte* pPath = System.Text.Encoding.UTF8.GetBytes(targetPath + "\0"))
            {
                got = rm->GetResourceSync((ResourceCategory*)&catRaw, &typeRaw, &hashRaw, pPath, null, null, 0);
            }
            nint gotN = (nint)got;
            sb.Append("\n  GetResourceSync => 0x").Append(gotN.ToString("X"))
              .Append(selfReload ? (gotN == live ? "  (== live handle — CACHE HIT ✓)" : "  (DIFFERENT handle?!)") : "");

            if (got != null && IsReadable(gotN, 0xC8))
            {
                uint gotSize = (uint)Marshal.ReadInt32(gotN + RhFileSizeOffset);
                uint gotRef = (uint)Marshal.ReadInt32(gotN + 0xAC);
                string gotPath = TryReadResourceFileName(gotN) ?? "<no name>";
                sb.Append("\n  got.file=\"").Append(gotPath).Append("\" size=").Append(gotSize).Append(" refCount=").Append(gotRef);
                // Dump the parsed EnvSet graph (ext+0xC0) so we can confirm the donor's own weatherId list built.
                ulong ext = (ulong)Marshal.ReadInt64(gotN + 0xC0);
                sb.Append("\n  ext+0xC0 = 0x").Append(ext.ToString("X"));
                if (ext >= PtrMin && ext <= PtrMax && IsReadable((nint)ext, 0x28))
                    DumpParsedEnvSet((nint)ext, sb);
                else
                    sb.Append("  (parsed EnvSet not yet built / unreadable)");

                // Balance the ref GetResourceSync added — VERIFY build does not retain the handle.
                got->DecRef();
                sb.Append("\n  DecRef() called (balanced the load ref).");
            }
            else
            {
                sb.Append("\n  GetResourceSync returned null/unreadable — load failed.");
            }

            log.Information(sb.ToString());
            return "[HMSync] wxloadverify: " + (selfReload ? "self-reload" : "donor load") + " of \"" + targetPath
                + "\" => handle 0x" + gotN.ToString("X") + (got != null ? " OK" : " FAILED") + ". Full trace in /xllog [WXLOAD].";
        }
        catch (Exception ex) { log.Error("[HMSync] wxloadverify failed: " + ex); return "[HMSync] wxloadverify failed: " + ex.Message; }
    }

    private static string FourCcToStr(uint v)
    {
        Span<char> c = stackalloc char[4];
        for (int i = 0; i < 4; i++) { byte b = (byte)(v >> (i * 8)); c[i] = b >= 0x20 && b < 0x7F ? (char)b : '.'; }
        return new string(c);
    }

    // sqpack path hash: crc32 (poly 0xEDB88320, init 0xFFFFFFFF). The game's resource `Id`/path hash uses the
    // NON-complemented running value (JAMCRC); we compute both forms and let LoadVerify pick the one matching the
    // live handle's Id, so we never rely on a remembered convention.
    private static uint SqPackCrc(ReadOnlySpan<byte> data, bool complement)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return complement ? ~crc : crc;
    }

    // b151: the ResourceHandleType.Expansion byte (bits 24-31) selects the sqpack repo and is path-derived — the first
    // `exN` path segment gives N (bg/ex2/... → 2). No exN segment (global/bgcommon) → 0. Used to fix cross-expansion
    // donor .tex loads in SwapSkyCubemap (borrowing the live handle's expansion byte read the wrong repo → size=0).
    private static byte ExpansionForPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        foreach (var seg in path.Split('/', '\\'))
        {
            if (seg.Length >= 3 && (seg[0] == 'e' || seg[0] == 'E') && (seg[1] == 'x' || seg[1] == 'X')
                && int.TryParse(seg.Substring(2), out int n) && n >= 0 && n <= 255)
                return (byte)n;
        }
        return 0;
    }

    // ── b140 (Path I: START the handle swap) ─────────────────────────────────────────────────────────────────────
    // Load `donorPath` (a full bgcommon .envb path that natively carries cycling city-spine weather), then repoint the
    // first live EnvSpace's +0x90 handle at it, mirror the donor's weatherId list into EnvScene.WeatherIds[32], and set
    // ActiveWeather to `donorWeather` (0 = auto-pick the donor's first slot). The game's UpdateEnvironment then samples
    // the donor keyframes natively → cycling. FIRST WRITE build; every written value was proven by b139. Returns a
    // user-facing status; the /xllog [WXCYCLE] line has the full trace.
    public string StartCycleCram(string donorPath, byte donorWeather, float timeSpeed)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(donorPath)) return "[HMSync] wxcyclecram: need a donor .envb path (or 'off').";
            if (cycleActive) StopCycleCram();   // one swap at a time

            var env = EnvManager.Instance();
            if (env == null) return "[HMSync] wxcyclecram: EnvManager null — in a zone?";
            nint scene = (nint)(*(nint*)((nint)env + EnvSceneOffset));
            if (scene == 0 || !IsReadable(scene, 0x890)) return "[HMSync] wxcyclecram: EnvScene null/unreadable.";

            // Source the native-call args (Type/FileType) off the current zone's own live handle, and remember which
            // EnvSpace slot we'll swap (the first live one).
            nint live = 0, spaceHandleAddr = 0;
            for (int s = 0; s < EnvSpaceCount; s++)
            {
                nint spaceBase = scene + EnvSpacesOffset + s * EnvSpaceSize;
                if (!IsReadable(spaceBase + EnvSetHandleOffset, 8)) continue;
                nint h = *(nint*)(spaceBase + EnvSetHandleOffset);
                if (h != 0 && IsReadable(h, 0xC8)) { live = h; spaceHandleAddr = spaceBase + EnvSetHandleOffset; break; }
            }
            if (live == 0) return "[HMSync] wxcyclecram: no live EnvSetResourceHandle to swap.";

            uint liveType = (uint)Marshal.ReadInt32(live + RhTypeOffset);
            uint fileType = (uint)Marshal.ReadInt32(live + RhFileTypeOffset);
            string donor = donorPath.Trim();
            uint donorHash = SqPackCrc(System.Text.Encoding.UTF8.GetBytes(donor.ToLowerInvariant()), true); // lower/complemented (b139-proven)

            var rm = ResourceManager.Instance();
            if (rm == null) return "[HMSync] wxcyclecram: ResourceManager null.";

            uint catRaw = liveType, typeRaw = fileType, hashRaw = donorHash;
            ResourceHandle* got;
            fixed (byte* pPath = System.Text.Encoding.UTF8.GetBytes(donor + "\0"))
                got = rm->GetResourceSync((ResourceCategory*)&catRaw, &typeRaw, &hashRaw, pPath, null, null, 0);
            nint donorHandle = (nint)got;
            if (got == null || !IsReadable(donorHandle, 0xC8))
                return "[HMSync] wxcyclecram: GetResourceSync failed for \"" + donor + "\".";

            // The parsed EnvSet MUST be built before we swap — never install a half-loaded handle.
            ulong ext = (ulong)Marshal.ReadInt64(donorHandle + 0xC0);
            if (ext < PtrMin || ext > PtrMax || !IsReadable((nint)ext, 0x28))
            { got->DecRef(); return "[HMSync] wxcyclecram: donor parsed EnvSet not built (ext+0xC0 unreadable) — aborted, DecRef'd."; }
            ulong vt = (ulong)Marshal.ReadInt64((nint)ext + 0x00);
            ushort setCount = (ushort)Marshal.ReadInt16((nint)ext + 0x20);
            bool vtOk = moduleBase != 0 && vt >= moduleBase && vt < moduleBase + 0x08000000UL;
            if (!vtOk || setCount == 0 || setCount > 32)
            { got->DecRef(); return "[HMSync] wxcyclecram: donor parsed EnvSet invalid (vtOk=" + vtOk + " setCount=" + setCount + ") — aborted, DecRef'd."; }

            // Donor per-slot weatherId list = arrayA (+0x10)'s first setCount bytes (b138-confirmed self-contained).
            nint arrayA = (nint)Marshal.ReadInt64((nint)ext + 0x10);
            var donorWids = new byte[32];
            if (arrayA != 0 && IsReadable(arrayA, setCount)) Marshal.Copy(arrayA, donorWids, 0, setCount);

            // Choose the weather to drive: caller's id if it's in the donor bank, else the donor's first slot.
            byte drive = 0;
            if (donorWeather != 0) { for (int i = 0; i < setCount; i++) if (donorWids[i] == donorWeather) { drive = donorWeather; break; } }
            if (drive == 0) drive = donorWids[0];

            // ── snapshot originals, then WRITE the swap ──
            got->IncRef();                                   // hold the donor for the cram's lifetime
            cycleDonorHandle = donorHandle;
            cycleSpaceHandleAddr = spaceHandleAddr;
            cycleSceneAddr = scene;
            cycleOrigHandle = *(nint*)spaceHandleAddr;
            cycleOrigWeatherIds = new byte[32];
            Marshal.Copy(scene + WeatherIdsOffset, cycleOrigWeatherIds, 0, 32);
            cycleOrigActive = env->ActiveWeather;
            cycleDonorWeather = drive;
            cycleDonorPath = donor;
            cycleRevertCount = 0;

            if (ReplayActive) SetReplay(false);              // Path A restamp would fight the native cycle — ensure off

            *(nint*)spaceHandleAddr = donorHandle;           // (1) repoint EnvSpace +0x90
            var mirror = new byte[32];                       // (2) mirror donor weatherIds (exact bank, rest zeroed)
            for (int i = 0; i < setCount && i < 32; i++) mirror[i] = donorWids[i];
            Marshal.Copy(mirror, 0, scene + WeatherIdsOffset, 32);
            env->ActiveWeather = drive;                      // (3) drive a cycling donor weather

            // b142: seed the self-driven day clock (overrides a fixed-time zone's DayTime pin so the set travels).
            cycleTimeSpeed = timeSpeed < 0 ? 0 : timeSpeed;
            cycleDriveTime = cycleTimeSpeed > 0;
            cycleVirtualDayTime = env->DayTimeSeconds;
            cycleLastTickMs = Environment.TickCount64;

            cycleActive = true;
            SyncHookState();   // b144: bring the UpdateEnvironment hook LIVE so the Detour can drive DayTimeSeconds pre-sample
            var widStr = new System.Text.StringBuilder();
            for (int i = 0; i < setCount; i++) { if (i > 0) widStr.Append(','); widStr.Append(donorWids[i]); }
            log.Information("[HMSync] [WXCYCLE] START donor=\"" + donor + "\" handle=0x" + donorHandle.ToString("X")
                + " setCount=" + setCount + " weatherIds=[" + widStr + "] drive=" + drive
                + " | swapped EnvSpace slot @0x" + spaceHandleAddr.ToString("X") + " (orig handle 0x" + cycleOrigHandle.ToString("X") + ")"
                + " | driveTime=" + cycleDriveTime + " speed=" + cycleTimeSpeed.ToString("0") + " seedDayTime=" + cycleVirtualDayTime.ToString("0"));
            return "[HMSync] wxcyclecram: swapped in donor bank \"" + System.IO.Path.GetFileName(donor) + "\" (setCount " + setCount
                + "), driving weather " + drive + (cycleDriveTime ? (", demo clock @" + cycleTimeSpeed.ToString("0") + "x (full day ~" + (86400f / cycleTimeSpeed).ToString("0") + "s)") : ", synced to real Eorzea time + slider")
                + ". `wxcyclecram off` to restore.";
        }
        catch (Exception ex) { log.Error("[HMSync] wxcyclecram start failed: " + ex); return "[HMSync] wxcyclecram start failed: " + ex.Message; }
    }

    // Per-frame re-assert (called from OnFrameworkUpdate). If the zone reasserted its own handle into +0x90, put ours
    // back (Principle 1: the game is the status reader — hold the swapped source rather than let it win). Cheap: only
    // writes when a value drifted. Also holds ActiveWeather on the donor weather so a native transition can't pull it
    // off the cycling slot.
    // b147: real Eorzea time-of-day in seconds [0,86400). This is the field the HMS time slider and Freeze drive (via
    // TimeFreezeService pinning ClientTime.EorzeaTime), so feeding it into the scene clock makes crammed city weather
    // cycle at the natural Eorzean rate AND honour the slider / Freeze — identical motion to a native city sky.
    private static float EorzeaTodSeconds()
    {
        var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        if (fw == null) return 0f;
        long et = fw->ClientTime.EorzeaTime;
        long tod = ((et % 86400) + 86400) % 86400;
        return (float)tod;
    }

    public void TickCycleCram()
    {
        if (!cycleActive) return;
        try
        {
            // Hold the SOURCE swap (the +0x90 handle pointer) — this is the data the sampler reads.
            if (cycleSpaceHandleAddr != 0 && IsReadable(cycleSpaceHandleAddr, 8))
            {
                nint cur = *(nint*)cycleSpaceHandleAddr;
                if (cur != cycleDonorHandle) { *(nint*)cycleSpaceHandleAddr = cycleDonorHandle; cycleRevertCount++; }
            }
            // b141: do NOT rewrite ActiveWeather every frame. Writing it re-kicks the weather TRANSITION (EnvMgr+0x14..
            // +0x28), and native time-of-day progression is gated behind the transition SETTLING — a per-frame rewrite
            // pins it "just changed" so it samples once at the reference time and never travels. Set once at Start; only
            // re-assert if it drifts FAR off (a real native weather change), not to hold the exact byte.
            var env = EnvManager.Instance();
            if (env == null) return;

            // b143 diagnostic: read DayTimeSeconds at the TOP of the tick, BEFORE our write. This is last frame's write as
            // it survived into this frame. If it reverted toward the zone's pin (36000s) the game RE-PINS it after us
            // (case A: ordering race — must write later, e.g. in a sampler-adjacent hook). If it still holds last frame's
            // virtual, our write STICKS but the sky still froze (case B: the resolved EnvState isn't re-sampled per-time).
            float preWrite = env->DayTimeSeconds;

            // b142: self-driven day clock. Fixed-time zones pin DayTimeSeconds (e.g. TT1097 @36000s = 10:00), so the
            // native sampler never walks the donor keyframes across the day. Advance our virtual clock by real-time
            // delta * speed, wrap at the 86400s Eorzean day, and write DayTimeSeconds so the sampler travels the set.
            if (cycleDriveTime)
            {
                long now = Environment.TickCount64;
                float d = (now - cycleLastTickMs) / 1000f;
                cycleLastTickMs = now;
                if (d > 0 && d < 5f)   // ignore huge deltas (alt-tab/stall) so we don't jump the clock
                {
                    cycleVirtualDayTime += d * cycleTimeSpeed;
                    while (cycleVirtualDayTime >= 86400f) cycleVirtualDayTime -= 86400f;
                    env->DayTimeSeconds = cycleVirtualDayTime;
                }
            }

            // b143: hash the resolved EnvState block (EnvMgr+0x58, 0x2F8 bytes) — this is the FINAL sky the renderer reads.
            // If this hash changes frame-to-frame the sampler IS producing a moving sky (so any "frozen" look is elsewhere);
            // if it's static while our clock advances, the resolved state is not being recomputed from time → case B.
            uint envHash = 0;
            nint envStateAddr = (nint)env + 0x58;
            if (IsReadable(envStateAddr, 0x2F8))
            {
                envHash = 2166136261u;
                byte* p = (byte*)envStateAddr;
                for (int i = 0; i < 0x2F8; i++) { envHash ^= p[i]; envHash *= 16777619u; }
            }

            // b143 trace: UNCONDITIONAL every ~60 frames (dropped the DayTime change-gate — if the game re-pins, DayTime
            // never changes and the old gate suppressed the very evidence we need). Logs preWrite (did our last write
            // survive?), virtual (what we intend), envHashChanged (is the resolved sky moving?), + transition + reverts.
            if (++cycleTickFrames >= 60)
            {
                cycleTickFrames = 0;
                bool envMoved = envHash != cycleLastEnvHash;
                cycleLastEnvHash = envHash;
                log.Information("[HMSync] [WXCYCLE-T] preWrite=" + preWrite.ToString("0")
                    + "s postWrite=" + env->DayTimeSeconds.ToString("0")
                    + "s drive=" + (cycleDriveTime ? "demo@" + cycleVirtualDayTime.ToString("0") : "eorzea@" + EorzeaTodSeconds().ToString("0"))
                    + " envState=" + envHash.ToString("X8") + (envMoved ? " (MOVED)" : " (static)")
                    + " detourPost=" + cycleDetourPostDayTime.ToString("0")
                    + "s sceneSecs=" + cycleSceneSecs.ToString("0") + " sceneHour=" + cycleSceneHour.ToString("0.0")
                    + " ActiveWeather=" + env->ActiveWeather + " (drive=" + cycleDonorWeather + ")"
                    + " transProgress=" + env->TransitionProgress.ToString("0.00")
                    + " reverts=" + cycleRevertCount);
            }
        }
        catch { /* never throw on the framework thread */ }
    }

    // Restore everything the swap changed and release the donor. Safe to call when inactive.
    public string StopCycleCram()
    {
        if (!cycleActive) return "[HMSync] wxcyclecram: not active.";
        int reverts = cycleRevertCount;
        try
        {
            // Restore the +0x90 slot ONLY if it still holds our donor (don't clobber a legit zone change).
            if (cycleSpaceHandleAddr != 0 && IsReadable(cycleSpaceHandleAddr, 8) && *(nint*)cycleSpaceHandleAddr == cycleDonorHandle)
                *(nint*)cycleSpaceHandleAddr = cycleOrigHandle;
            if (cycleSceneAddr != 0 && cycleOrigWeatherIds != null && IsReadable(cycleSceneAddr + WeatherIdsOffset, 32))
                Marshal.Copy(cycleOrigWeatherIds, 0, cycleSceneAddr + WeatherIdsOffset, 32);
            var env = EnvManager.Instance();
            if (env != null) env->ActiveWeather = cycleOrigActive;
            if (cycleDonorHandle != 0 && IsReadable(cycleDonorHandle, 0x10)) ((ResourceHandle*)cycleDonorHandle)->DecRef();
            log.Information("[HMSync] [WXCYCLE] STOP donor=\"" + cycleDonorPath + "\" — restored EnvSpace handle + WeatherIds + ActiveWeather=" + cycleOrigActive + ", DecRef'd donor. reverts=" + reverts);
        }
        catch (Exception ex) { log.Error("[HMSync] wxcyclecram stop failed: " + ex); }
        finally
        {
            cycleActive = false; cycleDonorHandle = 0; cycleSpaceHandleAddr = 0; cycleOrigHandle = 0;
            cycleSceneAddr = 0; cycleOrigWeatherIds = null; cycleDonorPath = ""; cycleDonorWeather = 0;
            cycleTickFrames = 0; cycleLastEnvHash = 0; cycleDetourPostDayTime = -1; cycleSceneSecs = -1; cycleSceneHour = -1;
            cycleDriveTime = false; cycleTimeSpeed = 0; cycleVirtualDayTime = 0; cycleLastTickMs = 0;
            SyncHookState();   // b144: cycle no longer wants the hook — drop it unless a scan/replay still does
        }
        return "[HMSync] wxcyclecram: restored (donor bank removed, native env back). zone reverts during cram: " + reverts + ".";
    }

    // ── b149 (Path I: SWAP the sky cubemap) ──────────────────────────────────────────────────────────────────────
    // Load `donorTexPath` (a full bg/.../envl/evlXXXX.tex city skybox), then repoint the first live EnvSpace's
    // EnvLocation cubemap: the resource handle (+0x98) AND the resolved GPU Texture* the renderer samples (+0xA8, read
    // from the loaded TextureResourceHandle.Texture @0x128). Writing both covers whether the renderer re-resolves +0xA8
    // from +0x98 each frame or caches it. Held (IncRef) for the swap's lifetime; TickSkySwap re-asserts if the zone
    // re-pins. Independent of the env cram (start the cram for the sun/colour cycle, then this for the stars).
    public string SwapSkyCubemap(string donorTexPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(donorTexPath)) return "[HMSync] wxskyswap: need a donor .tex path (or 'off').";
            if (skyActive) RestoreSkyCubemap();

            var env = EnvManager.Instance();
            if (env == null) return "[HMSync] wxskyswap: EnvManager null — in a zone?";
            nint scene = (nint)(*(nint*)((nint)env + EnvSceneOffset));
            if (scene == 0 || !IsReadable(scene, 0x8E0)) return "[HMSync] wxskyswap: EnvScene null/unreadable.";

            // First live EnvSpace whose EnvLocation carries a readable cubemap resource handle → the render sky owner.
            nint loc = 0, liveCubeRes = 0;
            for (int s = 0; s < EnvSpaceCount; s++)
            {
                nint spaceBase = scene + EnvSpacesOffset + s * EnvSpaceSize;
                if (!IsReadable(spaceBase + EnvSpaceLocationOffset, 8)) continue;
                nint l = *(nint*)(spaceBase + EnvSpaceLocationOffset);
                if (l == 0 || !IsReadable(l, 0xB0)) continue;
                nint cr = *(nint*)(l + ElCubemapResOffset);
                if (cr != 0 && IsReadable(cr, 0xC8)) { loc = l; liveCubeRes = cr; break; }
            }
            if (loc == 0) return "[HMSync] wxskyswap: no live EnvLocation with a cubemap resource to swap.";

            // Source the native-call args (category/FourCC) off the zone's own live .tex handle — correct for any .tex.
            // ResourceHandleType layout: Category@0 (ushort), Unknown@2 (byte), Expansion@3 (byte). The Expansion byte
            // (bits 24-31) selects the sqpack repo and is PATH-DEPENDENT: bg/ex2/... = 2, bg/ex4/... = 4, global = 0.
            // b151 FIX: the live handle is the dungeon's own .tex (e.g. ex4). A cross-expansion donor (Kugane = ex2) must
            // carry ITS OWN expansion byte or the loader reads the wrong repo → miss → size=0/loadState=9 (the b150 bug).
            // Preserve the live handle's Category+Unknown (both sky .tex are bg=2), override ONLY the expansion byte.
            uint liveType = (uint)Marshal.ReadInt32(liveCubeRes + RhTypeOffset);
            uint fileType = (uint)Marshal.ReadInt32(liveCubeRes + RhFileTypeOffset);
            string donor = donorTexPath.Trim();
            uint donorType = (liveType & 0x00FFFFFFu) | ((uint)ExpansionForPath(donor) << 24);
            uint donorHash = SqPackCrc(System.Text.Encoding.UTF8.GetBytes(donor.ToLowerInvariant()), true); // lower/complemented (b139-proven)

            var rm = ResourceManager.Instance();
            if (rm == null) return "[HMSync] wxskyswap: ResourceManager null.";

            uint catRaw = donorType, typeRaw = fileType, hashRaw = donorHash;
            ResourceHandle* got;
            fixed (byte* pPath = System.Text.Encoding.UTF8.GetBytes(donor + "\0"))
                got = rm->GetResourceSync((ResourceCategory*)&catRaw, &typeRaw, &hashRaw, pPath, null, null, 0);
            nint texHandle = (nint)got;
            if (got == null || !IsReadable(texHandle, 0x130))
                return "[HMSync] wxskyswap: GetResourceSync failed / handle too small for \"" + donor + "\".";

            // The .tex FILE loads synchronously, but the GPU texture UPLOAD (→ Texture@0x128) is deferred to the streamer.
            // Force it now via LoadIntoKernel (vfunc 31 = the texture override that builds the kernel texture). If it's
            // still not up (some builds keep it fully async), we DON'T abort: install the handle now and let TickSkySwap
            // poll Texture@0x128 and install +0xA8 the moment it resolves (b149-fix: async-aware, was a hard abort before).
            nint donorTex = (nint)Marshal.ReadInt64(texHandle + TexHandleResolvedTexOffset);
            if ((ulong)donorTex < PtrMin || (ulong)donorTex > PtrMax)
            {
                try { got->LoadIntoKernel(); } catch { }
                donorTex = (nint)Marshal.ReadInt64(texHandle + TexHandleResolvedTexOffset);
            }
            bool resolvedNow = (ulong)donorTex >= PtrMin && (ulong)donorTex <= PtrMax && IsReadable(donorTex, 0x10);

            uint gotSize = (uint)Marshal.ReadInt32(texHandle + RhFileSizeOffset);
            byte loadState = Marshal.ReadByte(texHandle + 0xA9);
            byte ioResult = Marshal.ReadByte(texHandle + 0x68);
            string gotPath = TryReadResourceFileName(texHandle) ?? "<no name>";

            got->IncRef();                                        // hold the donor for the swap's lifetime
            skyDonorTexHandle = texHandle;
            skyLocAddr = loc;
            skyOrigCubeResHandle = *(nint*)(loc + ElCubemapResOffset);
            skyOrigCubeTex = *(nint*)(loc + ElCubemapTexOffset);
            skyDonorPath = donor;
            skyRevertCount = 0;
            skyTexResolved = false;
            skyResolveFrames = 0;

            *(nint*)(loc + ElCubemapResOffset) = texHandle;       // (1) repoint the cubemap resource handle now
            if (resolvedNow)
            {
                *(nint*)(loc + ElCubemapTexOffset) = donorTex;    // (2) resolved → install the GPU cubemap the renderer samples
                skyTexResolved = true;
            }
            skyActive = true;

            log.Information("[HMSync] [WXSKY] SWAP donor=\"" + donor + "\" file=\"" + gotPath + "\" size=" + gotSize
                + " loadState=" + loadState + " ioResult=" + ioResult
                + " liveType=0x" + liveType.ToString("X8") + " donorType=0x" + donorType.ToString("X8")
                + " texHandle=0x" + texHandle.ToString("X") + " donorTex=0x" + donorTex.ToString("X")
                + (resolvedNow ? " (RESOLVED)" : " (async — deferred to tick)")
                + " loc=0x" + loc.ToString("X")
                + " (orig res=0x" + skyOrigCubeResHandle.ToString("X") + " origTex=0x" + skyOrigCubeTex.ToString("X") + ")");
            return "[HMSync] wxskyswap: swapped sky cubemap to \"" + System.IO.Path.GetFileName(donor) + "\""
                + (resolvedNow ? " (GPU texture installed)." : " — GPU texture resolving async, will install within a few frames.")
                + " `wxskyswap off` to restore.";
        }
        catch (Exception ex) { log.Error("[HMSync] wxskyswap failed: " + ex); return "[HMSync] wxskyswap failed: " + ex.Message; }
    }

    // Per-frame re-assert (called from OnFrameworkUpdate). Holds the swapped cubemap handle + resolved Texture* if the
    // zone re-pins its own. Cheap: only writes on drift.
    public void TickSkySwap()
    {
        if (!skyActive) return;
        try
        {
            if (skyLocAddr == 0 || !IsReadable(skyLocAddr, 0xB0)) return;
            // Hold the cubemap resource handle against any zone re-pin.
            if (*(nint*)(skyLocAddr + ElCubemapResOffset) != skyDonorTexHandle)
            { *(nint*)(skyLocAddr + ElCubemapResOffset) = skyDonorTexHandle; skyRevertCount++; }

            // Resolve the GPU texture: poll Texture@0x128. If still async, re-kick LoadIntoKernel every ~30 frames.
            nint donorTex = (nint)Marshal.ReadInt64(skyDonorTexHandle + TexHandleResolvedTexOffset);
            bool ready = (ulong)donorTex >= PtrMin && (ulong)donorTex <= PtrMax;
            if (ready)
            {
                if (*(nint*)(skyLocAddr + ElCubemapTexOffset) != donorTex)
                    *(nint*)(skyLocAddr + ElCubemapTexOffset) = donorTex;   // install / hold the resolved cubemap
                if (!skyTexResolved)
                {
                    skyTexResolved = true;
                    log.Information("[HMSync] [WXSKY] donor cubemap GPU texture RESOLVED @0x" + donorTex.ToString("X")
                        + " after " + skyResolveFrames + " frame(s) — installed at EnvLocation+0xA8.");
                }
            }
            else if (!skyTexResolved)
            {
                skyResolveFrames++;
                if (skyResolveFrames % 30 == 0)
                    try { ((ResourceHandle*)skyDonorTexHandle)->LoadIntoKernel(); } catch { }
                if (skyResolveFrames == 300)
                    log.Warning("[HMSync] [WXSKY] donor .tex GPU texture STILL unresolved after 300 frames — cubemap "
                        + "upload isn't taking; the star layer may not be the EnvLocation cubemap. See [WXSKY] SWAP line.");
            }
        }
        catch { /* never throw on the framework thread */ }
    }

    // Restore the native sky cubemap and release the donor. Safe to call when inactive.
    public string RestoreSkyCubemap()
    {
        if (!skyActive) return "[HMSync] wxskyswap: not active.";
        int reverts = skyRevertCount;
        try
        {
            if (skyLocAddr != 0 && IsReadable(skyLocAddr, 0xB0))
            {
                // Restore ONLY if the slot still holds ours (don't clobber a legit zone change).
                if (*(nint*)(skyLocAddr + ElCubemapResOffset) == skyDonorTexHandle)
                    *(nint*)(skyLocAddr + ElCubemapResOffset) = skyOrigCubeResHandle;
                nint donorTex = (nint)Marshal.ReadInt64(skyDonorTexHandle + TexHandleResolvedTexOffset);
                if (*(nint*)(skyLocAddr + ElCubemapTexOffset) == donorTex)
                    *(nint*)(skyLocAddr + ElCubemapTexOffset) = skyOrigCubeTex;
            }
            if (skyDonorTexHandle != 0 && IsReadable(skyDonorTexHandle, 0x10)) ((ResourceHandle*)skyDonorTexHandle)->DecRef();
            log.Information("[HMSync] [WXSKY] RESTORE donor=\"" + skyDonorPath + "\" — cubemap reverted, DecRef'd donor. reverts=" + reverts);
        }
        catch (Exception ex) { log.Error("[HMSync] wxskyswap restore failed: " + ex); }
        finally
        {
            skyActive = false; skyDonorTexHandle = 0; skyLocAddr = 0; skyOrigCubeResHandle = 0; skyOrigCubeTex = 0;
            skyDonorPath = ""; skyRevertCount = 0;
        }
        return "[HMSync] wxskyswap: restored native sky cubemap. reverts during swap: " + reverts + ".";
    }

    // ── b152 (Path I: SWAP the ambient set) ──────────────────────────────────────────────────────────────────────
    // Load `donorAmbPath` (a full bg/.../envl/evlXXXX.amb) and repoint the first live EnvSpace's EnvLocation ambient:
    // the resource handle (+0x90) AND the resolved AmbientSet* the lighting samples (+0xA0). The .amb handle interior is
    // unmapped in CS, so we DISCOVER where the resolved AmbientSet pointer lives inside the handle by scanning the LIVE
    // handle for a field == EnvLocation+0xA0 (the value we KNOW is the current resolved set), then read the donor's at the
    // same offset. Same cross-expansion type fix as the cubemap (ExpansionForPath). Held (IncRef) for the swap's lifetime.
    public string SwapAmbientSet(string donorAmbPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(donorAmbPath)) return "[HMSync] wxambswap: need a donor .amb path (or 'off').";
            if (ambActive) RestoreAmbientSet();

            var env = EnvManager.Instance();
            if (env == null) return "[HMSync] wxambswap: EnvManager null — in a zone?";
            nint scene = (nint)(*(nint*)((nint)env + EnvSceneOffset));
            if (scene == 0 || !IsReadable(scene, 0x8E0)) return "[HMSync] wxambswap: EnvScene null/unreadable.";

            // First live EnvSpace whose EnvLocation carries a readable ambient resource handle.
            nint loc = 0, liveAmbRes = 0;
            for (int s = 0; s < EnvSpaceCount; s++)
            {
                nint spaceBase = scene + EnvSpacesOffset + s * EnvSpaceSize;
                if (!IsReadable(spaceBase + EnvSpaceLocationOffset, 8)) continue;
                nint l = *(nint*)(spaceBase + EnvSpaceLocationOffset);
                if (l == 0 || !IsReadable(l, 0xB0)) continue;
                nint ar = *(nint*)(l + ElAmbientResOffset);
                if (ar != 0 && IsReadable(ar, 0xC8)) { loc = l; liveAmbRes = ar; break; }
            }
            if (loc == 0) return "[HMSync] wxambswap: no live EnvLocation with an ambient resource to swap.";

            nint liveResolvedSet = *(nint*)(loc + ElAmbientSetOffset);      // the KNOWN current resolved AmbientSet ptr

            // DISCOVER where inside the handle the resolved set pointer is stored: scan the live handle for liveResolvedSet.
            // (Textures keep theirs at +0x128; the .amb handle is 0xC8 and its layout is unmapped, so we find it.)
            int resolvedOff = -1;
            if ((ulong)liveResolvedSet >= PtrMin && (ulong)liveResolvedSet <= PtrMax)
            {
                for (int off = 0x40; off <= 0xC0; off += 8)
                {
                    if (Marshal.ReadInt64(liveAmbRes + off) == (long)liveResolvedSet) { resolvedOff = off; break; }
                }
            }

            uint liveType = (uint)Marshal.ReadInt32(liveAmbRes + RhTypeOffset);
            uint fileType = (uint)Marshal.ReadInt32(liveAmbRes + RhFileTypeOffset);
            string donor = donorAmbPath.Trim();
            uint donorType = (liveType & 0x00FFFFFFu) | ((uint)ExpansionForPath(donor) << 24); // b151 cross-expansion fix
            uint donorHash = SqPackCrc(System.Text.Encoding.UTF8.GetBytes(donor.ToLowerInvariant()), true);

            var rm = ResourceManager.Instance();
            if (rm == null) return "[HMSync] wxambswap: ResourceManager null.";

            uint catRaw = donorType, typeRaw = fileType, hashRaw = donorHash;
            ResourceHandle* got;
            fixed (byte* pPath = System.Text.Encoding.UTF8.GetBytes(donor + "\0"))
                got = rm->GetResourceSync((ResourceCategory*)&catRaw, &typeRaw, &hashRaw, pPath, null, null, 0);
            nint ambHandle = (nint)got;
            if (got == null || !IsReadable(ambHandle, 0xC8))
                return "[HMSync] wxambswap: GetResourceSync failed / handle too small for \"" + donor + "\".";

            uint gotSize = (uint)Marshal.ReadInt32(ambHandle + RhFileSizeOffset);
            byte loadState = Marshal.ReadByte(ambHandle + 0xA9);
            byte ioResult = Marshal.ReadByte(ambHandle + 0x68);
            string gotPath = TryReadResourceFileName(ambHandle) ?? "<no name>";

            // Read the donor's resolved AmbientSet at the discovered offset (if we found one).
            nint donorSet = 0;
            if (resolvedOff >= 0)
            {
                donorSet = (nint)Marshal.ReadInt64(ambHandle + resolvedOff);
                if ((ulong)donorSet < PtrMin || (ulong)donorSet > PtrMax) donorSet = 0;
            }
            bool resolvedNow = donorSet != 0;

            got->IncRef();
            ambDonorHandle = ambHandle;
            ambLocAddr = loc;
            ambOrigResHandle = *(nint*)(loc + ElAmbientResOffset);
            ambOrigSet = *(nint*)(loc + ElAmbientSetOffset);
            ambDonorPath = donor;
            ambRevertCount = 0;
            ambResolvedOff = resolvedOff;
            ambSetResolved = false;

            *(nint*)(loc + ElAmbientResOffset) = ambHandle;          // (1) repoint the ambient resource handle
            if (resolvedNow)
            {
                *(nint*)(loc + ElAmbientSetOffset) = donorSet;       // (2) install the resolved AmbientSet the lighting reads
                ambSetResolved = true;
            }
            ambActive = true;

            log.Information("[HMSync] [WXSKY] AMBSWAP donor=\"" + donor + "\" file=\"" + gotPath + "\" size=" + gotSize
                + " loadState=" + loadState + " ioResult=" + ioResult
                + " liveType=0x" + liveType.ToString("X8") + " donorType=0x" + donorType.ToString("X8")
                + " resolvedOff=" + (resolvedOff >= 0 ? "0x" + resolvedOff.ToString("X") : "<not found>")
                + " ambHandle=0x" + ambHandle.ToString("X") + " donorSet=0x" + donorSet.ToString("X")
                + (resolvedNow ? " (RESOLVED)" : " (resolved-ptr unknown — handle-only swap)")
                + " loc=0x" + loc.ToString("X")
                + " (orig res=0x" + ambOrigResHandle.ToString("X") + " origSet=0x" + ambOrigSet.ToString("X") + ")");
            return "[HMSync] wxambswap: swapped ambient set to \"" + System.IO.Path.GetFileName(donor) + "\""
                + (resolvedNow ? " (resolved set installed)." : " — resolved-ptr offset not found; handle-only (may not take).")
                + " `wxambswap off` to restore.";
        }
        catch (Exception ex) { log.Error("[HMSync] wxambswap failed: " + ex); return "[HMSync] wxambswap failed: " + ex.Message; }
    }

    // Per-frame re-assert: hold the swapped ambient handle + resolved set against a zone re-pin. Cheap: writes only on drift.
    public void TickAmbientSwap()
    {
        if (!ambActive) return;
        try
        {
            if (ambLocAddr == 0 || !IsReadable(ambLocAddr, 0xB0)) return;
            if (*(nint*)(ambLocAddr + ElAmbientResOffset) != ambDonorHandle)
            { *(nint*)(ambLocAddr + ElAmbientResOffset) = ambDonorHandle; ambRevertCount++; }

            if (ambResolvedOff >= 0)
            {
                nint donorSet = (nint)Marshal.ReadInt64(ambDonorHandle + ambResolvedOff);
                if ((ulong)donorSet >= PtrMin && (ulong)donorSet <= PtrMax)
                {
                    if (*(nint*)(ambLocAddr + ElAmbientSetOffset) != donorSet)
                        *(nint*)(ambLocAddr + ElAmbientSetOffset) = donorSet;
                    if (!ambSetResolved)
                    {
                        ambSetResolved = true;
                        log.Information("[HMSync] [WXSKY] donor AmbientSet RESOLVED @0x" + donorSet.ToString("X")
                            + " — installed at EnvLocation+0xA0.");
                    }
                }
            }
        }
        catch { /* never throw on the framework thread */ }
    }

    // Restore the native ambient set and release the donor. Safe to call when inactive.
    public string RestoreAmbientSet()
    {
        if (!ambActive) return "[HMSync] wxambswap: not active.";
        int reverts = ambRevertCount;
        try
        {
            if (ambLocAddr != 0 && IsReadable(ambLocAddr, 0xB0))
            {
                if (*(nint*)(ambLocAddr + ElAmbientResOffset) == ambDonorHandle)
                    *(nint*)(ambLocAddr + ElAmbientResOffset) = ambOrigResHandle;
                if (ambResolvedOff >= 0)
                {
                    nint donorSet = (nint)Marshal.ReadInt64(ambDonorHandle + ambResolvedOff);
                    if (*(nint*)(ambLocAddr + ElAmbientSetOffset) == donorSet)
                        *(nint*)(ambLocAddr + ElAmbientSetOffset) = ambOrigSet;
                }
            }
            if (ambDonorHandle != 0 && IsReadable(ambDonorHandle, 0x10)) ((ResourceHandle*)ambDonorHandle)->DecRef();
            log.Information("[HMSync] [WXSKY] AMBRESTORE donor=\"" + ambDonorPath + "\" — ambient reverted, DecRef'd donor. reverts=" + reverts);
        }
        catch (Exception ex) { log.Error("[HMSync] wxambswap restore failed: " + ex); }
        finally
        {
            ambActive = false; ambDonorHandle = 0; ambLocAddr = 0; ambOrigResHandle = 0; ambOrigSet = 0;
            ambDonorPath = ""; ambRevertCount = 0; ambResolvedOff = -1;
        }
        return "[HMSync] wxambswap: restored native ambient set. reverts during swap: " + reverts + ".";
    }

    // b145 diagnostic (wxenvdump): b144 proved driving DayTimeSeconds@0x10 no longer moves the sky on a fixed-time zone
    // (preWrite/detourPost track our virtual clock, yet EnvState@0x58 stays static). So 0x10 is NOT the sampler's effective
    // time input here — the real one is a DIFFERENT field (a second time copy, or a fixed-time value the sampler reads
    // directly). This scans EnvManager, its active EnvSpace (+0x38), and EnvScene (+0x08) for every TIME-SHAPED float —
    // matching seconds-of-day (~36000, i.e. |v-36000|<3000), hours (~10, |v-10|<0.75), or day-fraction (~0.417,
    // |v-0.417|<0.06). Run it DURING a cycle cram with the clock driven off 36000: the field the sampler actually uses
    // stays pinned near 36000 (or 10 / 0.417) while our driven 0x10 reads your advancing virtual time — that mismatch
    // fingerprints the true input. Read-only; no writes.
    public string EnvDump()
    {
        var em = EnvManager.Instance();
        if (em == null) return "[HMSync] wxenvdump: EnvManager null.";
        var sb = new System.Text.StringBuilder();
        int total = 0;
        total += ScanTimeFloats(sb, "EnvManager", (nint)em, 0xBF0);
        if (em->EnvSpace != null) total += ScanTimeFloats(sb, "EnvSpace(+0x38)", (nint)em->EnvSpace, 0xF0);
        if (em->EnvScene != null) total += ScanTimeFloats(sb, "EnvScene(+0x08)", (nint)em->EnvScene, 0x8F0);
        float drv = em->DayTimeSeconds;
        log.Information("[HMSync] [WXENVDUMP] DayTimeSeconds@0x10=" + drv.ToString("0.##")
            + " (drive off 36000 to disambiguate). Time-shaped floats:\n" + (sb.Length == 0 ? "  (none)" : sb.ToString()));
        return "[HMSync] wxenvdump: logged " + total + " time-shaped float(s) to /xllog. DayTime@0x10=" + drv.ToString("0")
            + ". During a driven cycle cram, any field still ≈36000 (or ≈10 / ≈0.417) while 0x10 tracks your virtual clock is the sampler's REAL time input.";
    }

    private int ScanTimeFloats(System.Text.StringBuilder sb, string label, nint baseAddr, int size)
    {
        if (baseAddr == 0 || !IsReadable(baseAddr, size)) return 0;
        int found = 0;
        for (int off = 0; off + 4 <= size; off += 4)
        {
            float v = *(float*)(baseAddr + off);
            if (!float.IsFinite(v)) continue;
            string? kind = null;
            if (Math.Abs(v - 36000f) < 3000f) kind = "secs";
            else if (Math.Abs(v - 10f) < 0.75f) kind = "hour";
            else if (Math.Abs(v - 0.4167f) < 0.06f) kind = "frac";
            if (kind == null) continue;
            sb.Append("  ").Append(label).Append("+0x").Append(off.ToString("X3"))
              .Append('=').Append(v.ToString("0.####")).Append(" (").Append(kind).Append(")\n");
            found++;
        }
        return found;
    }

    // b164 diagnostic (wxsimdump): the RE-INTERPOLATE GATE hunt. The handle-swap cram proved that on a fixed-time zone the
    // native sampler FREEZES EnvState (does not re-interpolate the sky from the swapped .envb each frame) while a city
    // re-interpolates every frame — that freeze is why we adopted keyframe RECORDING (storage cost). If we can find the
    // memory flag/state that gates native re-interpolation and flip it on a fixed-time target, we get native cycling from
    // the ~KB .envb (zero storage, perfect fidelity). Prime suspect: EnvSimulator (EnvManager+0x4E0, size 0x3C0, BLANK in
    // CS) plus the EnvManager transition fields (0x14/0x18/0x1C/0x28). This is a READ-ONLY dump. OPERATOR PROTOCOL: run it
    // once in a TIME-VARYING CITY (e.g. Kugane — let the clock advance, fields should be ALIVE/changing) and once on a
    // FIXED-TIME DUNGEON with a cram/graft driving the clock (e.g. Lapis Manalis 1097 — the interpolation state should be
    // FROZEN). Diff the two /xllog blocks: a field that is live in the city but pinned/zero in the dungeon is the gate.
    public string SimDump()
    {
        var em = EnvManager.Instance();
        if (em == null) return "[HMSync] wxsimdump: EnvManager null — in a zone?";
        nint eb = (nint)em;
        var sb = new System.Text.StringBuilder();

        // Lens validation (skill: validate the lens before trusting relative offsets). If EnvScene+0x80 ≈ DayTimeSeconds@0x10
        // the base is right and every dumped offset below is trustworthy; a wild mismatch means don't trust this dump.
        float dts = em->DayTimeSeconds;
        var sc0 = em->EnvScene;
        float sceneSecs = (sc0 != null && IsReadable((nint)sc0 + 0x80, 4)) ? *(float*)((nint)sc0 + 0x80) : float.NaN;
        sb.Append("  LENS: DayTimeSeconds@0x10=").Append(dts.ToString("0.##"))
          .Append("  EnvScene+0x80=").Append(sceneSecs.ToString("0.##"))
          .Append(float.IsFinite(sceneSecs) && Math.Abs(sceneSecs - dts) < 200f ? "  [lens OK]" : "  [LENS MISMATCH — distrust]").Append('\n');

        // Named EnvManager transition/weather fields — the interpolation-driver candidates.
        sb.Append("  EnvManager transition fields:\n");
        sb.Append("    +0x10 DayTimeSeconds     =").Append(ReadF(eb + 0x10).ToString("0.####")).Append('\n');
        sb.Append("    +0x14 ActiveTransition   =").Append(ReadF(eb + 0x14).ToString("0.####")).Append('\n');
        sb.Append("    +0x18 CurrentTransition  =").Append(ReadF(eb + 0x18).ToString("0.####")).Append('\n');
        sb.Append("    +0x1C TransitionProgress =").Append(ReadF(eb + 0x1C).ToString("0.####")).Append('\n');
        sb.Append("    +0x27 ActiveWeather      =").Append(IsReadable(eb + 0x27, 1) ? (*(byte*)(eb + 0x27)).ToString() : "?").Append('\n');
        sb.Append("    +0x28 TransitionTime     =").Append(ReadF(eb + 0x28).ToString("0.####")).Append('\n');

        // EnvSimulator window (em+0x4E0, size 0x3C0) — BLANK in CS. Dump every LIVE 4-byte slot two ways so the diff can
        // catch either representation: as a finite non-trivial float, and as a small int / bitflag. Zeros are omitted to
        // keep the diff readable (a slot that's 0 in both zones tells us nothing; a slot 0 in one and set in the other
        // still surfaces because it prints in the zone where it's non-zero).
        const int simOff = 0x4E0;
        const int simSize = 0x3C0;
        int simLive = 0;
        sb.Append("  EnvSimulator (+0x4E0, 0x3C0) live slots:\n");
        if (IsReadable(eb + simOff, simSize))
        {
            for (int off = 0; off + 4 <= simSize; off += 4)
            {
                uint raw = *(uint*)(eb + simOff + off);
                if (raw == 0) continue;
                float fv = *(float*)(eb + simOff + off);
                string fstr = float.IsFinite(fv) && Math.Abs(fv) > 1e-6f && Math.Abs(fv) < 1e9f ? fv.ToString("0.####") : "-";
                sb.Append("    +0x").Append((simOff + off).ToString("X3"))
                  .Append(" (sim+0x").Append(off.ToString("X3")).Append(")  u32=0x").Append(raw.ToString("X8"))
                  .Append("  i32=").Append(((int)raw).ToString())
                  .Append("  f=").Append(fstr).Append('\n');
                simLive++;
            }
        }
        else sb.Append("    (EnvSimulator window not readable)\n");

        log.Information("[HMSync] [WXSIMDUMP] re-interpolate gate hunt (READ-ONLY). Diff CITY(alive) vs DUNGEON(driven):\n" + sb.ToString());
        return "[HMSync] wxsimdump: logged " + simLive + " live EnvSimulator slot(s) + transition fields to /xllog. Run once in a "
            + "time-varying CITY and once on a fixed-time DUNGEON with the clock driven, then diff the two /xllog blocks — a slot "
            + "alive in the city but frozen/zero in the dungeon is the re-interpolate gate.";
    }

    private float ReadF(nint addr) => IsReadable(addr, 4) ? *(float*)addr : float.NaN;

    // b165: FNV-1a over a memory block (same constants as the b143 cycle-hash). Returns 0 if unreadable.
    private uint HashBlock(nint addr, int size)
    {
        if (addr == 0 || !IsReadable(addr, size)) return 0;
        uint h = 2166136261u;
        byte* p = (byte*)addr;
        for (int i = 0; i < size; i++) { h ^= p[i]; h *= 16777619u; }
        return h;
    }

    // b165 freeze-probe control (wxfreezeprobe [sweep]). START: observe EnvState per frame + change-gate log (sweep also
    // self-drives the clock across the day so a fixed-time zone actually moves). STOP (toggle again): print the verdict —
    // the distinct-hash-change count over the run. ~1 change ⇒ FROZEN (EnvState did not re-interpolate as time swept →
    // native cycling isn't available here, the recording path stays); many changes ⇒ RE-INTERPOLATING (the sampler tracks
    // the driven clock, so the fixed-time freeze is downstream/recoverable). Read-only apart from the clock writes.
    public string ToggleFreezeProbe(bool sweep)
    {
        if (hook == null) return "[HMSync] wxfreezeprobe: UpdateEnvironment hook unavailable (sig scan failed at ctor).";
        if (freezeProbeActive)
        {
            freezeProbeActive = false;
            SyncHookState();
            string verdict = freezeProbeChanges <= 2
                ? "VERDICT: FROZEN — EnvState did NOT re-interpolate as the clock swept (only " + freezeProbeChanges
                  + " change(s)). Native cycling is not available on this zone; the recording path stays. (Caveat: assumes "
                  + "0x10/EnvScene+0x80/+0x54 are the sampler's clock — b162 proved they drive the render's time.)"
                : "VERDICT: RE-INTERPOLATING — EnvState changed " + freezeProbeChanges + "x as the clock swept. The native "
                  + "sampler DOES track the driven clock here → the fixed-time freeze is downstream and may be recoverable.";
            log.Information("[HMSync] [WXFREEZEPROBE] STOP. frames=" + freezeProbeFrames + " hash-changes=" + freezeProbeChanges
                + " sweep=" + freezeProbeSweep + " -> " + verdict);
            return "[HMSync] wxfreezeprobe: STOP. frames=" + freezeProbeFrames + " hash-changes=" + freezeProbeChanges + ". " + verdict;
        }
        freezeProbeActive = true;
        freezeProbeSweep = sweep;
        freezeProbeHasLast = false;
        freezeProbeLastHash = 0;
        freezeProbeChanges = 0;
        freezeProbeFrames = 0;
        freezeProbeVirtualSecs = EorzeaTodSeconds();
        SyncHookState();
        return "[HMSync] wxfreezeprobe: START (" + (sweep
            ? "SWEEP — self-driving the clock across the full day; watch the sky travel or stay frozen"
            : "OBSERVE — move time yourself (HMS time slider) and watch") + "). "
            + "Trace in /xllog [WXFREEZEPROBE]; run `wxfreezeprobe` again to STOP and print the verdict.";
    }

    // Snapshot the live EnvState block (call while the DONOR weather renders — e.g. after setweather 150 on 958).
    public string Capture()
    {
        try
        {
            var env = EnvManager.Instance();
            if (env == null) return "[HMSync] wxcapture: EnvManager null — in a zone?";
            byte active = env->ActiveWeather;
            var buf = new byte[EnvStateSize];
            Marshal.Copy((nint)env + EnvStateOffset, buf, 0, EnvStateSize);
            captured = buf;
            capturedWeather = active;
            ProbeResourceWords(buf, active);   // b116: dump avfx-handle offset + .avfx path for this weather (Tier-2 data)
            ConfigureRestampFor(active, buf, live: true);
            return "[HMSync] wxcapture: snapshot of EnvState taken under weather " + active
                + ". Travel to a zone lacking it, then `wxreplay on`.";
        }
        catch (Exception ex) { return "[HMSync] wxcapture failed: " + ex.Message; }
    }

    // Snapshot the live EnvState and RETURN the raw 0x2F8 bytes (for baking into the preset library). Also stores it
    // as the active captured block so `wxreplay on` works immediately after a bake. Null if EnvManager unavailable.
    public byte[]? CaptureRaw(out byte activeWeather)
    {
        activeWeather = 0;
        try
        {
            var env = EnvManager.Instance();
            if (env == null) return null;
            activeWeather = env->ActiveWeather;
            var buf = new byte[EnvStateSize];
            Marshal.Copy((nint)env + EnvStateOffset, buf, 0, EnvStateSize);
            captured = buf;
            capturedWeather = activeWeather;
            ProbeResourceWords(buf, activeWeather);   // b116: dump avfx-handle offset + .avfx path per weather (wxbakeall/wxbaketour → full-suite log)
            ConfigureRestampFor(activeWeather, buf, live: true);
            return buf;
        }
        catch (Exception ex) { log.Error("[HMSync] WeatherCramService.CaptureRaw failed: " + ex.Message); return null; }
    }

    // Load a preset blob (from the library) as the captured block and start replaying it. This is the preset-driven
    // path (vs. Capture()'s live-snapshot path): a peer applies a synced weather id -> its embedded blob -> here.
    public string ApplyBlob(byte[] blob, byte id, System.Collections.Generic.IReadOnlyList<DoodadBake>? doodads = null, bool strip = false)
    {
        if (hook == null) return "[HMSync] wxpreset: UpdateEnvironment hook unavailable (sig scan failed at ctor).";
        if (blob == null || blob.Length != EnvStateSize)
            return "[HMSync] wxpreset: bad preset size " + (blob?.Length.ToString() ?? "null") + " (expected " + EnvStateSize + ").";
        captured = (byte[])blob.Clone();
        capturedWeather = id;
        // b120 Tier-2 (candidate A): if the preset carries baked doodad descriptors, RE-ESTABLISH them from live buffers
        // and go WHOLESALE — so a persisted/synced preset spawns its avfx (meteors etc.) without a same-session live
        // donor pointer. This is what makes tapping a foreign-weather chip render full effects, not sky-only. The old
        // "persisted blob → stale pointer → force selective" rule only applies when NO doodads are baked (sky-only preset).
        if (doodads != null && doodads.Count > 0)
        {
            // b131: strip=true (strip-list ids, e.g. 208 Floracane) zeroes the descriptor's inner heap-pointer words to
            // sever the second-order dangler that CTD'd 207/208, while keeping the inline emitters (petals). Faithful copy
            // otherwise. See MapSettingsService.StripDoodadIds.
            int n = ReestablishDoodads(doodads, strip: strip, tag: "WXCRAM");
            log.Information("[HMSync] [WXCRAM] preset " + id + " applied WHOLESALE with " + n + " re-established doodad(s)"
                + (strip ? " (STRIP inner ptrs)" : "") + ".");
        }
        else
        {
            ConfigureRestampFor(id, captured, live: false);   // no baked doodads → selective sky-only (crash-safe)
        }
        return SetReplay(true);
    }

    // Toggle post-recompute replay. Enables the hook only while active (like TimeFreezeService) to avoid overhead.
    public string SetReplay(bool? on = null)
    {
        if (hook == null) return "[HMSync] wxreplay: UpdateEnvironment hook unavailable (sig scan failed at ctor).";
        if (captured == null) return "[HMSync] wxreplay: nothing captured — run `wxcapture` first.";
        bool want = on ?? !ReplayActive;
        ReplayActive = want;
        if (want) loggedTarget = false;
        SyncHookState();   // b136: keep the hook up if a wxtimescan is still armed even when the cram goes off
        return "[HMSync] wxreplay: " + (want ? "ON" : "OFF") + " (captured weather " + capturedWeather + ").";
    }

    public void Dispose()
    {
        try { if (starForceActive) StopStarForce(); } catch { }   // b155: restore forced StarRenderer params
        try { if (ambActive) RestoreAmbientSet(); } catch { }   // b152: restore the swapped ambient set + DecRef donor .amb
        try { if (skyActive) RestoreSkyCubemap(); } catch { }   // b149: restore the swapped sky cubemap + DecRef donor .tex
        try { if (cycleActive) StopCycleCram(); } catch { }   // b140: restore the swapped EnvSpace handle + DecRef donor
        try { if (hook is { IsEnabled: true }) hook.Disable(); } catch { }
        hook?.Dispose();
        FreeColdAllocs();   // b119: hook is down (replay stopped) → no live EnvState word points at these; safe to free
        // b130 wxdooddiag: tear down the fault VEH + close the forensic log.
        try { if (vehHandle != 0) { RemoveVectoredExceptionHandler(vehHandle); vehHandle = 0; } } catch { }
        vehDelegate = null;
        try { faultLog?.Flush(); faultLog?.Dispose(); } catch { }
        faultLog = null;
    }
}
