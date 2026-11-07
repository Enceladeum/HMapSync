using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using LayoutTransform = FFXIVClientStructs.FFXIV.Client.LayoutEngine.Transform;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace HMSync.World;

// ============================================================================
// BgSpawner - places background models into the live world and owns them until Clear().
//
// WORLD-LAYER PRIMITIVE: depends only on Dalamud services + ClientStructs, never on HMS services, so it can
// graduate into a shared Enceladeum.World library unchanged. Ported from a proven collision-editor spawn path
// (SpawnModelAt / CreateFurnitureAt / SpawnApplyTick / TeardownModel / TeardownSGL), which is the Brio/Intoner
// convergence:
//   .mdl -> BgObject.Create        (bare scene object, NOT registered with the LayoutManager)
//   .sgb -> FurnitureCreate -> SharedGroupLayoutInstance (registered layout instance; composes the group)
//
// Three crash rules carried over verbatim:
//  1. READY-GATE: never write SRT / UpdateTransforms / UpdateCulling before the model has loaded
//     (LoadState >= 7, or IsPrimaryLoaded for a group). Touching earlier AVs in ComputeSphereBounds+0xE.
//     Objects are created hidden and landed + revealed by Tick() once ready.
//  2. NEVER route an .sgb through BgObject.Create (single-.mdl loader -> CDT). No .mdl fallback for groups.
//  3. TEAR DOWN BEFORE THE ZONE UNLOADS. A bare BgObject isn't in the LayoutManager, so a zone unload frees the
//     model under a still-registered render/cull object. The original flushed AFTER it saw the territory change
//     (a race); callers of this class must call Clear() BEFORE the load starts (HMS: ZoneWillChange).
//
// Threading: framework thread only (spawn, Tick, Clear).
// ============================================================================
public sealed unsafe class BgSpawner : IDisposable
{
    private const int ReadyMaxTicks = 600;   // ~10s @ 60fps; a model that never loads (bad path, redirected away) is given up on, left hidden

    private struct Spawned
    {
        public nint Ptr;
        public bool Group;          // SharedGroupLayoutInstance (true) or BgObject (false)
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 Scale;
        public bool Landed;         // transform applied + revealed
        public int Ticks;           // ready-gate wait
        public string Path;
    }

    private readonly IPluginLog log;
    private readonly byte[] poolName;
    private readonly List<Spawned> spawned = new();

    // FurnitureCreate / native SharedGroup destroy (sigs from Brio, byte-identical across Brio/Intoner).
    private readonly delegate* unmanaged<short, uint, LayoutTransform*, byte*, byte*, byte, uint, int, nint, nint, SharedGroupLayoutInstance*> createGroup;
    private readonly delegate* unmanaged<SharedGroupLayoutInstance**, nint, void> destroyGroup;

    /// <summary>True when the .sgb (SharedGroup) path resolved; .mdl spawning never depends on it.</summary>
    public bool GroupsAvailable { get; }

    public int Count => spawned.Count;
    public int LandedCount { get { int n = 0; foreach (var s in spawned) if (s.Landed) n++; return n; } }

    public BgSpawner(IPluginLog log, ISigScanner sigScanner, string poolName)
    {
        this.log = log;
        this.poolName = System.Text.Encoding.UTF8.GetBytes(poolName + "\0");
        try
        {
            var create = sigScanner.ScanText("E8 ?? ?? ?? ?? 48 89 04 ?? C6 44");
            var destroy = sigScanner.ScanText("48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 48 8B 19 48 8B F9 48 8B F2");
            createGroup = (delegate* unmanaged<short, uint, LayoutTransform*, byte*, byte*, byte, uint, int, nint, nint, SharedGroupLayoutInstance*>)create;
            destroyGroup = (delegate* unmanaged<SharedGroupLayoutInstance**, nint, void>)destroy;
            GroupsAvailable = create != 0 && destroy != 0;
        }
        catch (Exception ex)
        {
            GroupsAvailable = false;
            log.Warning("[BgSpawner] SharedGroup sig scan failed, .sgb spawning disabled: " + ex.Message);
        }
    }

    /// <summary>Spawn a .mdl or .sgb at a world transform. Returns false if it couldn't be created.</summary>
    public bool Spawn(string path, Vector3 pos, Quaternion rot, Vector3 scale)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.EndsWith(".sgb", StringComparison.OrdinalIgnoreCase)) return SpawnGroup(path, pos, rot, scale);
        if (path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) return SpawnModel(path, pos, rot, scale);
        log.Warning("[BgSpawner] unsupported asset type: " + path);
        return false;
    }

    private bool SpawnModel(string path, Vector3 pos, Quaternion rot, Vector3 scale)
    {
        BgObject* obj;
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(path + "\0");
            fixed (byte* p = bytes) fixed (byte* pool = poolName)
                obj = BgObject.Create(p, pool, null);
        }
        catch (Exception ex) { log.Warning("[BgSpawner] BgObject.Create threw for " + path + ": " + ex.Message); return false; }
        if (obj == null) return false;
        // Create doesn't guarantee a zero outline (a stale one leaves the copy glowing). Plain field write, safe pre-load.
        ((DrawObject*)obj)->OutlineColor = 0;
        // Hidden until Tick lands it: a fresh object sits at Create's default origin with no valid transform.
        ((DrawObject*)obj)->IsVisible = false;
        spawned.Add(new Spawned { Ptr = (nint)obj, Group = false, Pos = pos, Rot = rot, Scale = scale, Path = path });
        return true;
    }

    private bool SpawnGroup(string path, Vector3 pos, Quaternion rot, Vector3 scale)
    {
        if (!GroupsAvailable) { log.Warning("[BgSpawner] .sgb skipped (native group path unavailable): " + path); return false; }
        SharedGroupLayoutInstance* sgl;
        try
        {
            var t = new LayoutTransform { Translation = pos, Rotation = rot, Scale = scale };
            var bytes = System.Text.Encoding.UTF8.GetBytes(path + "\0");
            fixed (byte* p = bytes)
                // (layer=-1 global, key=0 auto, &transform, path, secondaryPath=null, flags=1, 0, type=0xC sgb-attach, parent=0, boneAttach=0): Brio's set
                sgl = createGroup(-1, 0, &t, p, null, 1, 0, 0xC, 0, 0);
            if (sgl != null) sgl->SetTransformImpl(&t);   // stores SRT only, safe pre-load
        }
        catch (Exception ex) { log.Warning("[BgSpawner] FurnitureCreate threw for " + path + ": " + ex.Message); return false; }
        if (sgl == null) return false;
        spawned.Add(new Spawned { Ptr = (nint)sgl, Group = true, Pos = pos, Rot = rot, Scale = scale, Path = path });
        return true;
    }

    /// <summary>Per-frame: land + reveal every spawned object whose resource has finished loading.</summary>
    public void Tick()
    {
        for (int i = 0; i < spawned.Count; i++)
        {
            var s = spawned[i];
            if (s.Landed || s.Ticks < 0) continue;
            if (!IsReady(s))
            {
                if (++s.Ticks >= ReadyMaxTicks)
                {
                    log.Warning("[BgSpawner] never loaded after " + ReadyMaxTicks + " ticks, left hidden: " + s.Path);
                    s.Ticks = -1;   // stop polling; still torn down by Clear
                }
                spawned[i] = s;
                continue;
            }
            try
            {
                if (s.Group)
                {
                    // Children have streamed in: push the stored SRT down onto them (ApplyTransforms walks child
                    // graphics, the touch-before-loaded surface the gate protects).
                    var sgl = (SharedGroupLayoutInstance*)s.Ptr;
                    var t = new LayoutTransform { Translation = s.Pos, Rotation = s.Rot, Scale = s.Scale };
                    sgl->SetTransformImpl(&t);
                    sgl->Instances.ApplyTransforms();
                }
                else
                {
                    var o = (SceneObject*)s.Ptr;
                    o->Position = s.Pos; o->Rotation = s.Rot; o->Scale = s.Scale;
                    var d = (DrawObject*)s.Ptr;
                    d->UpdateTransforms(false);   // the renderer reads the cached matrices, not the raw SRT
                    d->UpdateCulling();
                    d->IsVisible = true;
                }
            }
            catch (Exception ex) { log.Warning("[BgSpawner] land threw for " + s.Path + ": " + ex.Message); }
            s.Landed = true;
            spawned[i] = s;
        }
    }

    private static bool IsReady(Spawned s)
    {
        if (s.Group)
        {
            try { return ((SharedGroupLayoutInstance*)s.Ptr)->IsPrimaryLoaded(); }
            catch { return false; }
        }
        var obj = (BgObject*)s.Ptr;
        return obj->ModelResourceHandle != null && obj->ModelResourceHandle->LoadState >= 7;
    }

    /// <summary>
    /// Destroy everything this spawner owns, synchronously. Call BEFORE a zone change begins (rule 3).
    /// Ladder per object: hide, unregister from render/cull (CleanupRender), destruct; groups use the
    /// engine's own Deinit + native destroy (Brio's sequence).
    /// </summary>
    public void Clear()
    {
        if (spawned.Count == 0) return;
        int n = spawned.Count;
        foreach (var s in spawned)
        {
            try
            {
                if (s.Group)
                {
                    var sgl = (SharedGroupLayoutInstance*)s.Ptr;
                    sgl->Deinit();
                    destroyGroup(&sgl, 0);
                }
                else
                {
                    var d = (DrawObject*)s.Ptr;
                    d->IsVisible = false;
                    d->CleanupRender();
                    ((SceneObject*)s.Ptr)->Dtor(1);
                }
            }
            catch (Exception ex) { log.Warning("[BgSpawner] teardown threw for " + s.Path + ": " + ex.Message); }
        }
        spawned.Clear();
        log.Information("[BgSpawner] cleared " + n + " spawned object(s)");
    }

    public void Dispose() => Clear();
}
