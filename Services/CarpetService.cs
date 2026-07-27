using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace HMSync.Services;

// ============================ GROUND CARPET ============================
// Ported from HCollider (CARPET_HANDOFF.md). A toggle that lets the player walk on surfaces with no
// collision mesh (out-of-bounds roofs, gaps, the void) by laying a short trail of overlapping flat
// collider patches under the player each game tick — dropping a new one ahead of their movement,
// retiring old ones behind. Patches are REAL engine colliders (created via the game's own SceneWrapper
// factory), so the player physically stands and walks on them. Purely client-side; paired with HMS's
// packet filter there is no server desync.
//
// Headline flow: fly in on a mount to altitude, toggle on (first patch spawns at the mount's feet so
// you land naturally), dismount, walk — the carpet extends under you.
//
// Load-bearing design choices (do NOT regress — see the handoff):
//   • Trail of STATIC patches, never a moving disc. Repositioning the floor under you mid-frame makes
//     the physics engine jitter/launch/drop you. Add ahead, retire behind, never move a live one.
//   • Never retire the patch the player is currently over (horizontal distance check vs radius*1.1).
//   • Velocity from POSITION DELTA, not facing — captures true movement (strafe/backwalk), unlike
//     GameObject.Rotation.
//   • Forward cap, speed-scaled — bias the patch ahead along actual movement so the leading edge stays
//     in front of the feet (prevents walking off the front edge at speed).
//   • Box patches only (S320c) — the cylinder option wedged the capsule on dismount / mid-air enable /
//     jumps; the box doesn't. Square joins tile cleanly with the speed-scaled forward cap.
//   • Pitch -0.05 = flat — Position.Y is the foot origin; a thin box is centred on its origin, so -0.05
//     puts the top face exactly at foot level. Positive Pitch climbs, negative descends. The FIRST patch
//     uses DropOffset instead (a separate first-only offset for a cinematic drop-in); the rest use Pitch.
//
// HMS-specific improvements over the HCollider original (per the handoff's open items):
//   • Real frame delta (IFramework.UpdateDelta) for velocity/lead instead of the framerate-naive *60.
//   • S320: turned OFF (+ notified) on any HMS-driven zone change via ZoneLoadService.ZoneWillChange
//     (HMS's own lifecycle — LoadZone for /hms load, the load detour for normal teleports, and the
//     leave teardown for /hms stop|leave). Carpet is a map-specific convenience, so it does NOT carry
//     across a zone load: loading into a new zone with a flat trail still active would glide you off
//     the first staircase. Full disable beats clear-and-keep, and re-enabling is a trivial cost.
//     (Driven by HMS, NOT the Dalamud TerritoryChanged event — that fires after the fact and couples
//     the carpet to an external signal it shouldn't own.)
public sealed unsafe class CarpetService
{
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly HMSyncConfig config;

    // ---- baked-in defaults (the `/hms carpet` preset) ----
    public bool On { get; private set; }
    public bool ShowRings = true;       // orientation rings on by default (onboarding aid)
    public float Radius = 1.3f;         // patch radius (yalms)
    public float Step = 2.2f;           // drop a new patch after moving this far from the last centre
    public int Trail = 5;               // max live patches
    public float LeadBase = 1.0f;       // forward-cap base offset
    public float LeadPerSpeed = 0.25f;  // additional lead per unit speed (speed-scaled cap)
    // S320c: the WALKING SLOPE. Each ongoing patch sits at footY + Pitch. Flat (level walking) is -0.05 —
    // the thin box's top face exactly at foot level. Positive = uphill (each patch higher, player climbs),
    // negative = downhill. Replaces the old YOffset value + FlatLock checkbox: flat is now a Reset button,
    // with Uphill/Downhill presets at the clearance limit for the default radius/step.
    public float Pitch = DefaultPitch;
    // S320c: Y offset for the FIRST patch only (the one laid on enable). Defaults to feet; can be set well
    // below feet for a cinematic "drop in from altitude" entrance. Every patch AFTER the first follows Pitch.
    public float DropOffset = DefaultPitch;

    public const float DefaultPitch = -0.05f;   // flat: box top face exactly at foot level
    public const float UphillPitch = 0.40f;     // max uphill step clearable at the default radius/step
    public const float DownhillPitch = -0.40f;  // symmetric downhill (kept symmetric for easy backtracking)
    public const float MinRadius = 1.0f;        // below this, strafing can cross the patch edge → falls
    public const float MinStep = 1.0f;          // below this, join spacing lets strafe slip through → falls

    // ---- live tracking ----
    private readonly List<nint> patches = new();    // live patch colliders (oldest first), Collider* as nint
    private readonly List<Vector3> centers = new();  // parallel centres
    private Vector3 lastPos;                          // last-frame player position (for velocity)
    private bool havePos;
    private bool subscribed;

    public CarpetService(IObjectTable objectTable, IFramework framework, IPluginLog log, HMSyncConfig config)
    {
        this.objectTable = objectTable;
        this.framework = framework;
        this.log = log;
        this.config = config;
    }

    public Action<string>? StatusReport { get; set; }

    // Optional guard so the carpet goes inert during a zone load/revert teardown window (set by the
    // plugin to ZoneLoadService.IsTransitioning), mirroring NoclipService.TransitionGuard.
    public Func<bool>? TransitionGuard { get; set; }

    public void Initialize()
    {
        LoadTunablesFromConfig();
    }

    // S319: pull persisted tunables into the live fields (called on init).
    private void LoadTunablesFromConfig()
    {
        ShowRings = config.CarpetShowRings;
        Radius = Math.Max(MinRadius, config.CarpetRadius);   // enforce the anti-fall floor even on old configs
        Step = Math.Max(MinStep, config.CarpetStep);
        Trail = config.CarpetTrail;
        LeadBase = config.CarpetLeadBase;
        LeadPerSpeed = config.CarpetLeadPerSpeed;
        Pitch = config.CarpetPitch;
        DropOffset = config.CarpetDropOffset;
    }

    // S319: write the live tunables back to config + save. Called by the UI whenever a control changes.
    public void SaveTunables()
    {
        config.CarpetShowRings = ShowRings;
        config.CarpetRadius = Radius;
        config.CarpetStep = Step;
        config.CarpetTrail = Trail;
        config.CarpetLeadBase = LeadBase;
        config.CarpetLeadPerSpeed = LeadPerSpeed;
        config.CarpetPitch = Pitch;
        config.CarpetDropOffset = DropOffset;
        config.Save();
    }

    // /hms carpet — toggle with the baked-in flat preset. Applies defaults on enable.
    public void Toggle()
    {
        if (On) Disable();
        else Enable();
    }

    public void Enable()
    {
        if (On) return;
        On = true;
        havePos = false;
        if (!subscribed)
        {
            framework.Update += OnFrameworkTick;
            subscribed = true;
        }
        log.Information("[HMSync] Carpet ON (radius=" + Radius.ToString("F1") + " step=" + Step.ToString("F1") + " trail=" + Trail + ").");
        StatusReport?.Invoke("[HMSync] Carpet ON.");
    }

    public void Disable()
    {
        if (!On) return;
        On = false;
        Clear();
        if (subscribed)
        {
            framework.Update -= OnFrameworkTick;
            subscribed = false;
        }
        log.Information("[HMSync] Carpet OFF.");
        StatusReport?.Invoke("[HMSync] Carpet OFF.");
    }

    // Reset every tunable to the baked-in preset and persist.
    public void ResetToDefaults()
    {
        ShowRings = true;
        Radius = 1.3f;
        Step = 2.2f;
        Trail = 5;
        LeadBase = 1.0f;
        LeadPerSpeed = 0.25f;
        Pitch = DefaultPitch;
        DropOffset = DefaultPitch;
        SaveTunables();
    }

    // S320c: pitch presets (the "walk up / walk down without fiddling" crutch) + flat reset. All persist.
    public void SetPitchFlat()     { Pitch = DefaultPitch;  SaveTunables(); }
    public void SetPitchUphill()   { Pitch = UphillPitch;   SaveTunables(); }
    public void SetPitchDownhill() { Pitch = DownhillPitch; SaveTunables(); }
    // S320c: drop-offset reset → back to feet.
    public void ResetDropOffset()  { DropOffset = DefaultPitch; SaveTunables(); }

    // Per-frame: maintain the trail. Static patches, add-ahead / retire-behind. (See class header.)
    private void OnFrameworkTick(IFramework fw)
    {
        if (!On) return;
        if (TransitionGuard != null && TransitionGuard()) return;   // inert during zone load/revert teardown

        var local = objectTable.LocalPlayer;
        if (local is null) return;
        var pos = local.Position;

        // Real per-second velocity from position delta over the actual frame delta (HMS improvement over
        // the framerate-naive *60). Position delta captures strafe/backwalk; facing would not.
        float dt = (float)fw.UpdateDelta.TotalSeconds;
        Vector3 vel = Vector3.Zero;
        if (havePos && dt > 1e-4f) vel = (pos - lastPos) / dt;   // units / second
        lastPos = pos; havePos = true;
        float speed = vel.Length();

        // Forward cap: bias the patch centre along actual movement, scaled by speed (now units/sec), so
        // the leading edge stays ahead of the feet. Circle shape means this works for any heading.
        Vector3 lead = Vector3.Zero;
        if (speed > 1e-3f)
        {
            var dir = vel / speed;
            lead = dir * (LeadBase + speed * LeadPerSpeed);
        }
        var target = pos + lead;

        // Flat ground at foot level (+ offset). The carpet's job is footing where none exists; the
        // surface-conforming downward-ray variant was built and removed (stuttered on stairs, flattened
        // descents) — flat-at-feet is both simpler and the behaviour actually wanted for unwired roofs.
        // S320c: the FIRST patch (none yet) uses DropOffset (cinematic-drop control); every patch after
        // follows Pitch (the walking slope — flat / uphill / downhill).
        float yOff = (centers.Count == 0) ? DropOffset : Pitch;
        var center = new Vector3(target.X, pos.Y + yOff, target.Z);

        // Drop a new patch when we've moved past the step threshold from the last centre (or none yet).
        bool needPatch = centers.Count == 0
            || Vector3.Distance(new Vector3(center.X, 0, center.Z),
                                new Vector3(centers[^1].X, 0, centers[^1].Z)) > Step;
        if (!needPatch) return;

        var live = CreatePatch(center);
        if (live != null)
        {
            patches.Add((nint)live);
            centers.Add(center);
        }

        // Retire oldest beyond trail length — but never one the player is still standing on/near.
        while (patches.Count > Trail)
        {
            var c0 = centers[0];
            float horiz = Vector3.Distance(new Vector3(pos.X, 0, pos.Z), new Vector3(c0.X, 0, c0.Z));
            if (horiz <= Radius * 1.1f) break;   // still on/near it — keep
            RemoveColliderPtr((Collider*)patches[0]);
            patches.RemoveAt(0);
            centers.RemoveAt(0);
        }
    }

    // Re-drop the most recent patch at the current Pitch relative to the player's feet, so a patch can
    // be dropped underfoot and dialled to exact height live from the UI.
    public void AdjustActiveTileY()
    {
        if (patches.Count == 0) return;
        var local = objectTable.LocalPlayer;
        if (local is null) return;
        int last = patches.Count - 1;
        var c = centers[last];
        var newCenter = new Vector3(c.X, local.Position.Y + Pitch, c.Z);
        RemoveColliderPtr((Collider*)patches[last]);
        patches[last] = (nint)CreatePatch(newCenter);
        centers[last] = newCenter;
    }

    public void Clear()
    {
        for (int i = 0; i < patches.Count; i++)
            RemoveColliderPtr((Collider*)patches[i]);
        patches.Clear();
        centers.Clear();
        havePos = false;
    }

    // S316: read-only snapshot of live patch centres for the orientation-ring overlay. Copies into the
    // caller's list (no alloc per frame if the caller reuses the buffer); returns the count.
    public int SnapshotCenters(List<Vector3> into)
    {
        into.Clear();
        for (int i = 0; i < centers.Count; i++) into.Add(centers[i]);
        return into.Count;
    }

    // Create one flat box patch at the given centre via the game's OWN factory (SceneWrapper.AddColliderBox).
    // The engine allocates from its per-type pool, inserts into the BVH, and owns teardown — a supported
    // runtime op. Scale = (radius, 0.05 thickness, radius). Returns the live Collider*.
    // S320c: box only. The cylinder option was removed — it stuck the actor on dismount, on mid-air
    // activation, and on some jumps (the rounded collision let the capsule wedge); the box has none of that.
    private Collider* CreatePatch(Vector3 center)
    {
        var module = CSFramework.Instance()->BGCollisionModule;
        if (module == null || module->SceneManager == null) return null;
        var sw = module->SceneManager->FirstScene;
        if (sw == null) return null;

        var pos = center;
        var rot = Vector3.Zero;
        var scl = new Vector3(Radius, 0.05f, Radius);
        ulong lm = 1ul;

        return (Collider*)sw->AddColliderBox(lm, &pos, &rot, &scl);
    }

    // Remove a single collider via the engine, but only if it's still actually in the scene — avoids a
    // double-free if a zone change already reclaimed it.
    private void RemoveColliderPtr(Collider* coll)
    {
        if (coll == null) return;
        var module = CSFramework.Instance()->BGCollisionModule;
        if (module == null || module->SceneManager == null) return;
        var sw = module->SceneManager->FirstScene;
        if (sw == null) return;
        bool live = false;
        foreach (var c in sw->Scene->Colliders) if (c == coll) { live = true; break; }
        if (live) sw->RemoveCollider(coll);
    }

    public void Dispose()
    {
        if (subscribed)
        {
            framework.Update -= OnFrameworkTick;
            subscribed = false;
        }
        Clear();
    }
}
