using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace HMSync.Services;

// v0.7.351: constructive collision — add a flat box collider ("floor patch") to fill an unmeshed area (e.g. the o1e1
// observation deck). This is the CONSTRUCTIVE counterpart to the scene-suppression toolkit: instead of hiding/moving
// existing colliders, it spawns NEW ones via the engine's own factory (SceneWrapper.AddColliderBox) — the exact
// proven op CarpetService already uses for its moving floor patches. The engine allocates from its per-type pool,
// inserts into the BVH, and owns teardown; RemoveCollider drops it. All patches are removed on stop.
//
// Workflow (place-and-see): /hms deckfloor drops a patch at the player's current position with the current size, so
// you stand where the floor should be and see if it catches you; /hms deckfloor size <x> <z> and /hms deckfloor
// thick <t> tune the extents; /hms deckfloor clear removes them. Once a patch is dialed in, its center+size can be
// baked as a static patch for that stage (like the door targets).
public sealed unsafe class DeckFloorService
{
    private readonly IPluginLog log;
    private readonly Func<Vector3?> getPlayerPos;   // current player position (for place-at-feet)

    private readonly List<nint> patches = new();   // live Collider* we created via /hms deckfloor (manual)
    private float sizeX = 4f, sizeZ = 4f, thick = 0.1f;

    // --- baked static patches (auto-applied per cutscene stage, like the door targets) ---
    private struct BakedPatch { public Vector3 Center; public float SizeX; public float SizeZ; public float Thick; }
    // Note: AddColliderBox positions by CENTER; the walkable TOP surface = Center.Y + Thick. So to put the floor at a
    // target height H, bake Center.Y = H - Thick. o1e1 observation deck: floor at Y=4.613, thick 0.1 → center Y=4.513.
    private static readonly Dictionary<string, BakedPatch[]> StagePatches = new()
    {
        ["ffxiv/ocn_o1/evt/o1e1/level/o1e1"] = new[]
        {
            // Observation deck (below/aft of the opened doors). Trapezoid ~12.6 wide × ~7.9 deep; first pass covers the
            // bounding rectangle (overhang past the narrowing far edge is invisible). Center = true XZ midpoint of the
            // four corners; Y = 4.613 floor − 0.1 thick = 4.513.
            new BakedPatch { Center = new Vector3(0.0f, 4.513f, -29.898f), SizeX = 13.0f, SizeZ = 8.2f, Thick = 0.1f },
        },
    };
    private string? appliedPatchStage;              // which stage's baked patches are currently live
    private readonly List<nint> stagePatchColliders = new();

    // Idempotent per-tick call from the plugin: ensure the baked patches for `stageBg` are applied (once), and cleared
    // when the stage changes or becomes null. Mirrors the door pass's stage-gating, but a collider is created ONCE
    // (not re-asserted every frame).
    public void EnsureStagePatches(string? stageBg)
    {
        if (appliedPatchStage == stageBg) return;   // already in the right state
        // stage changed (or cleared) → drop the old stage's patches
        if (stagePatchColliders.Count > 0)
        {
            foreach (var p in stagePatchColliders) RemoveBox((Collider*)p);
            stagePatchColliders.Clear();
        }
        appliedPatchStage = stageBg;
        if (stageBg == null || !StagePatches.TryGetValue(stageBg, out var baked)) return;
        foreach (var bp in baked)
        {
            var c = CreateBox(bp.Center, new Vector3(bp.SizeX * 0.5f, bp.Thick, bp.SizeZ * 0.5f));
            if (c != null) stagePatchColliders.Add((nint)c);
        }
        if (stagePatchColliders.Count > 0)
            log.Information("[HMSync] [DECKFLOOR] baked " + stagePatchColliders.Count + " patch(es) for stage " + stageBg);
    }

    public DeckFloorService(IPluginLog log, Func<Vector3?> getPlayerPos)
    {
        this.log = log;
        this.getPlayerPos = getPlayerPos;
    }

    // Drop a patch centered at `center`. Returns true on success.
    public bool AddPatch(Vector3 center)
    {
        var c = CreateBox(center, new Vector3(sizeX * 0.5f, thick, sizeZ * 0.5f));
        if (c == null) { log.Information("[HMSync] [DECKFLOOR] AddColliderBox returned null (scene not ready?)."); return false; }
        patches.Add((nint)c);
        log.Information("[HMSync] [DECKFLOOR] patch @ (" + center.X.ToString("F3") + "," + center.Y.ToString("F3") + "," +
            center.Z.ToString("F3") + ") size=(" + sizeX + "," + thick + "," + sizeZ + ")  total=" + patches.Count);
        return true;
    }

    // Place a patch at the player's current feet position.
    public void AddPatchAtPlayer()
    {
        var p = getPlayerPos();
        if (p == null) { log.Information("[HMSync] [DECKFLOOR] no player position."); return; }
        AddPatch(p.Value);
    }

    public void SetSize(float x, float z) { sizeX = MathF.Max(0.2f, x); sizeZ = MathF.Max(0.2f, z); log.Information("[HMSync] [DECKFLOOR] size set to (" + sizeX + " x " + sizeZ + ")."); }
    public void SetThickness(float t) { thick = MathF.Max(0.02f, t); log.Information("[HMSync] [DECKFLOOR] thickness set to " + thick + "."); }

    public void Clear()
    {
        int n = patches.Count;
        foreach (var p in patches) RemoveBox((Collider*)p);
        patches.Clear();
        // also drop any baked stage patches
        foreach (var p in stagePatchColliders) RemoveBox((Collider*)p);
        stagePatchColliders.Clear();
        appliedPatchStage = null;
        log.Information("[HMSync] [DECKFLOOR] cleared " + n + " manual patch(es) + baked stage patches.");
    }

    public int Count => patches.Count;

    // --- engine factory (mirrors CarpetService.CreatePatch / RemoveColliderPtr) ---

    private Collider* CreateBox(Vector3 center, Vector3 halfExtents)
    {
        var module = CSFramework.Instance()->BGCollisionModule;
        if (module == null || module->SceneManager == null) return null;
        var sw = module->SceneManager->FirstScene;
        if (sw == null) return null;
        var pos = center;
        var rot = Vector3.Zero;
        var scl = halfExtents;
        ulong lm = 1ul;   // same layer mask the carpet uses — participates in normal walk collision
        return (Collider*)sw->AddColliderBox(lm, &pos, &rot, &scl);
    }

    private void RemoveBox(Collider* coll)
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
}
