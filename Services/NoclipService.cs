using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace HMSync.Services;

/// <summary>
/// Two movement modes:
///
/// 1. Flight (/hms fly) - hooks IsFlightProhibited, game-native smooth flight.
///    Respects collision. Jump to enter flight, WASD to move.
///
/// 2. Noclip (/hms noclip) - manual SetPosition using Win32 GetAsyncKeyState
///    for input (bypasses game's input system entirely). Ignores collision.
///    WASD moves relative to camera, Space up, Shift down.
///    Use to pass through walls, reach blocked areas.
///
/// (Removed: the clamp/altitude-lock, dismounted flight-speed, and flat-flight (W-pitch)
/// experiments, all of which hooked RMIWalk/RMIFly. None worked - W-forward flight is hard-bound
/// to the camera look-vector and could not be flattened via the fly input (Up-injection inverts;
/// 0x9C is a non-causal pitch cache; DirMode probe inconclusive). The intended replacement is
/// hot-spawned ground collision under the actor (HCollider) so the engine treats it as grounded.
/// If per-frame movement interception is revisited, the vnavmesh RMIWalk/RMIFly sigs + fly-input
/// struct are journalled.)
/// </summary>
public unsafe class NoclipService : IDisposable
{
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider hookProvider;

    // Flight hook - IsFlightProhibited returns a flight-allowed status (int).
    // Returning 0 from the detour forces "flight allowed" while FlightActive.
    private delegate int IsFlightProhibitedDelegate();
    private Hook<IsFlightProhibitedDelegate>? flightHook;

    // (Movement-input hooks RMIWalk/RMIFly were used for the removed clamp/altitude-lock, flight-speed,
    // and flat-flight (W-pitch) experiments - all abandoned. None worked: W-forward flight is hard-bound
    // to the camera look-vector and could not be flattened via the fly input. Flight is now purely the
    // IsFlightProhibited hook below. The vnavmesh RMIWalk/RMIFly sigs + fly-input struct are journalled
    // if per-frame movement interception is ever revisited; the future direction is hot-spawned ground
    // collision under the actor (HCollider) so the engine treats the actor as grounded - no input hook.)

    // Win32 key state
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    // Key codes
    private const int VK_W = 0x57;
    private const int VK_A = 0x41;
    private const int VK_S = 0x53;
    private const int VK_D = 0x44;
    private const int VK_SPACE = 0x20;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_SHIFT = 0x10;

    public bool FlightActive { get; private set; }
    public bool NoclipActive { get; private set; }
    public float NoclipSpeed { get; set; } = 0.5f;

    // Status reporter - set by the plugin so messages print to chat like the slash commands.
    public Action<string>? StatusReport { get; set; }

    // Transition guard predicate - set by the plugin; true while a zone load/revert is in progress.
    public Func<bool>? TransitionGuard { get; set; }

    // Cached game PID (the S234 lesson: don't call GetCurrentProcess() every frame; it allocates).
    private uint gamePid;

    public NoclipService(
        IObjectTable objectTable,
        IFramework framework,
        IPluginLog log,
        ISigScanner sigScanner,
        IGameInteropProvider hookProvider)
    {
        this.objectTable = objectTable;
        this.framework = framework;
        this.log = log;
        this.sigScanner = sigScanner;
        this.hookProvider = hookProvider;
    }

    public void Initialize()
    {
        try
        {
            var addr = sigScanner.ScanText(
                "40 53 48 83 EC 20 48 8B 1D ?? ?? ?? ?? 48 85 DB 0F 84 ?? ?? ?? ?? 80 3D");
            flightHook = hookProvider.HookFromAddress<IsFlightProhibitedDelegate>(addr, IsFlightProhibitedDetour);
            flightHook.Enable();
            log.Information("[HMSync] Flight hook created");
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] Flight hook failed: " + ex.Message);
        }

        // Cache the game PID once (used by focus checks; GetCurrentProcess allocates).
        try { gamePid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id; }
        catch (Exception ex) { log.Error("[HMSync] PID cache failed: " + ex.Message); }
    }

    // ── Flight mode ──
    //
    // Flight is just native flight (IsFlightProhibited hook). It does NOT own any return logic: the
    // only "return" in HMS is ZoneLoadService.Revert (reload origin zone + origin coords), which is the
    // inverse of loading a foreign zone. Plain /hms fly with no HMS zone loaded is ordinary flight in
    // your current zone - stopping it just stops flight where you are, nothing to return from.

    public void ToggleFlight()
    {
        if (FlightActive)
        {
            FlightActive = false;
            log.Information("[HMSync] Flight mode OFF");
        }
        else
        {
            FlightActive = true;
            log.Information("[HMSync] Flight mode ON - jump to fly");
        }
    }

    // S204: idempotent enable/disable for auto-flight-on-mount. Enabling flight-readiness is SAFE on
    // any mount - FlightActive only makes IsFlightProhibited return 0 (flight-allowed); a ground-only
    // mount can't fly regardless, so it's a no-op there, and a flight-capable mount becomes ready to
    // take off on Space. Called from MountSelf so /hms mount on a flying mount auto-arms flight.
    public void EnableFlight()
    {
        if (!FlightActive)
        {
            FlightActive = true;
            log.Information("[HMSync] Flight armed on mount (Space to take off).");
        }
    }

    public void DisableFlight()
    {
        if (FlightActive)
        {
            FlightActive = false;
            log.Information("[HMSync] Flight disarmed (dismounted).");
        }
    }

    private int IsFlightProhibitedDetour()
    {
        try
        {
            if (FlightActive) return 0;
        }
        catch (Exception ex) { log.Debug("[HMSync] Flight detour: " + ex.Message); }
        return flightHook!.Original();
    }

    // ── Noclip mode ──

    public void ToggleNoclip()
    {
        if (NoclipActive)
        {
            NoclipActive = false;
            framework.Update -= OnNoclipUpdate;
            log.Information("[HMSync] Noclip OFF");
        }
        else
        {
            NoclipActive = true;
            framework.Update += OnNoclipUpdate;
            log.Information("[HMSync] Noclip ON");
        }
    }

    private void OnNoclipUpdate(IFramework fw)
    {
        if (!NoclipActive) return;

        // Don't process noclip keys when typing in chat/ImGui or game is not focused
        var imguiIo = Dalamud.Bindings.ImGui.ImGui.GetIO();
        if (imguiIo.WantTextInput) return;

        // Check if game window is in foreground. NB-27: use the cached gamePid (set once in Enable) instead of
        // System.Diagnostics.Process.GetCurrentProcess() - this runs every frame while noclip is active, and
        // GetCurrentProcess() allocates a Process object per call (the exact S234 lesson called out at gamePid's
        // declaration). Comparing the foreground PID against the cached value is allocation-free and identical.
        var gameWindow = GetForegroundWindow();
        GetWindowThreadProcessId(gameWindow, out var foregroundPid);
        if (foregroundPid != gamePid) return;

        var player = objectTable.LocalPlayer;
        if (player == null) return;

        var native = (GameObject*)player.Address;
        var pos = player.Position;

        // ── #1 (S325): HEADING SOURCE - character, not camera ──────────────────────────────────────────────────────
        // Classic noclip rotated WASD by the CAMERA heading, so orbiting the camera (LMB) changed your W-direction -
        // disorienting. We want W relative to CHARACTER FACING, exactly like normal locomotion: RMB-steer (which
        // rotates the character) changes your W-direction; LMB-orbit (camera only) does NOT.
        //
        // ANGLE DERIVATION (not a guess - from the codebase's own convention): StateApplyService (~L1010) computes
        // movement direction as Atan2(dx, dz) and compares it to GameObject.Rotation directly, so the forward vector
        // is (sin(Rotation), 0, cos(Rotation)) - standard FFXIV facing. RotatePoint does x'=x·cos-z·sin, z'=x·sin+z·cos;
        // feeding a local +Z step (0,step) rotated by `angle` gives world (-step·sin(angle), step·cos(angle)). For that
        // to equal forward (step·sin(rot), step·cos(rot)) we need angle = -Rotation. So we pass NEGATIVE heading.
        var chara = (Character*)player.Address;
        var heading = -chara->Rotation;

        // NOTE: an "Ignore Walls" (slow/capped noclip-through-walls) mode was explored S325–S325e and REMOVED - it
        // never worked and two diagnostic builds crashed the client. Root understanding: noclip is position-teleport
        // (SetPosition, no collision consult) and floor-catch is EMERGENT (the engine re-grounds the actor each tick),
        // so it's a tug-of-war - classic's big 0.5/frame step out-paces re-grounding and crosses walls, but small cruise
        // steps lose and get blocked. The command/config/UI were stripped; it's a research item in the roadmap (§A / P2).
        // If revived: SUPPRESS the actor's collider (à la the dungeon entry-ring barrier work, SetColliderActive(false))
        // so SetPosition passes at any speed without the tug-of-war, and decide gravity/floor explicitly - with careful,
        // crash-safe instrumentation (change-gated + heavily throttled; per-frame gait reads were suspected in the CTDs).
        float step = NoclipSpeed;   // classic free-speed noclip

        bool moved = false;

        // Win32 GetAsyncKeyState - reads actual keyboard hardware state, bypasses the game's input system.

        // ── VERTICAL (Space up / Shift down) - classic noclip ──
        if (IsKeyDown(VK_SPACE))
        {
            pos.Y += step;
            moved = true;
        }
        if (IsKeyDown(VK_LSHIFT) || IsKeyDown(VK_SHIFT))
        {
            pos.Y -= step;
            moved = true;
        }

        if (IsKeyDown(VK_W))
        {
            pos = RotatePoint(pos.X, pos.Z, heading, pos + new Vector3(0, 0, step));
            moved = true;
        }

        if (IsKeyDown(VK_S))
        {
            pos = RotatePoint(pos.X, pos.Z, heading, pos + new Vector3(0, 0, -step));
            moved = true;
        }

        if (IsKeyDown(VK_A))
        {
            pos = RotatePoint(pos.X, pos.Z, heading, pos + new Vector3(step, 0, 0));
            moved = true;
        }

        if (IsKeyDown(VK_D))
        {
            pos = RotatePoint(pos.X, pos.Z, heading, pos + new Vector3(-step, 0, 0));
            moved = true;
        }

        if (moved)
            native->SetPosition(pos.X, pos.Y, pos.Z);
    }

    private void Report(string msg)
    {
        if (StatusReport != null) StatusReport(msg);
        else log.Information(msg);
    }

    private static Vector3 RotatePoint(float cx, float cy, float angle, Vector3 p)
    {
        if (angle == 0f) return p;
        var s = MathF.Sin(angle);
        var c = MathF.Cos(angle);
        p.X -= cx;
        p.Z -= cy;
        float xnew = p.X * c - p.Z * s;
        float znew = p.X * s + p.Z * c;
        p.X = xnew + cx;
        p.Z = znew + cy;
        return p;
    }

    // IsActive: convenience aggregate used by the plugin to report/clear movement state.
    public bool IsActive => FlightActive || NoclipActive;

    // S285: full movement-state sanitize. Called on every return (stop/leave/disconnect) so NO session
    // condition leaks into the next session. Resets EVERY toggleable/settable movement state here -
    // when a new movement condition is added, it MUST be reset here too (single sanitize point). The
    // OnNoclipUpdate handler is detached.
    public void Disable()
    {
        if (FlightActive) FlightActive = false;
        if (NoclipActive)
        {
            NoclipActive = false;
            framework.Update -= OnNoclipUpdate;
        }
    }

    public void Dispose()
    {
        Disable();
        flightHook?.Dispose();
    }
}
