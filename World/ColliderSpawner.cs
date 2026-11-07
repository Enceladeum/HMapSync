using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using NVector3 = System.Numerics.Vector3;

namespace HMSync.World;

// ============================================================================
// ColliderSpawner - adds collision meshes (.pcb) to the live BG collision scene and owns them until Clear().
//
// WORLD-LAYER PRIMITIVE (no HMS dependencies; graduates to Enceladeum.World with BgSpawner). Same lever as the 1345
// road-clone (ZoneLoadService.ApplyRoadClones1345: SceneWrapper.AddColliderMesh) and the same live-checked removal as
// CarpetService.RemoveColliderPtr: a collider is only removed if it is still in the scene's list, so a scene the game
// already tore down can never be touched. Clear() must run BEFORE the zone unloads (with BgSpawner.Clear).
// Rotation is the layout's Euler (radians), the form level colliders are built from. Framework thread only.
// ============================================================================
public sealed unsafe class ColliderSpawner : IDisposable
{
    private readonly IPluginLog log;
    private readonly List<nint> colliders = new();

    public int Count => colliders.Count;

    public ColliderSpawner(IPluginLog log) => this.log = log;

    private static SceneWrapper* Scene()
    {
        var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        return fw != null && fw->BGCollisionModule != null && fw->BGCollisionModule->SceneManager != null
            ? fw->BGCollisionModule->SceneManager->FirstScene : null;
    }

    /// <summary>Add a collision mesh at a transform. False if the scene isn't up or the engine refused it.</summary>
    public bool Add(string pcbPath, NVector3 pos, NVector3 rotEuler, NVector3 scale)
    {
        var sw = Scene();
        if (sw == null || string.IsNullOrEmpty(pcbPath)) return false;
        NVector3 p = pos, r = rotEuler, s = scale;
        var bytes = System.Text.Encoding.UTF8.GetBytes(pcbPath + "\0");
        try
        {
            fixed (byte* path = bytes)
            {
                var c = (Collider*)sw->AddColliderMesh(1ul, path, false, &p, &r, &s);
                if (c == null) return false;
                colliders.Add((nint)c);
                return true;
            }
        }
        catch (Exception ex) { log.Warning("[ColliderSpawner] AddColliderMesh threw for " + pcbPath + ": " + ex.Message); return false; }
    }

    public void Clear()
    {
        if (colliders.Count == 0) return;
        var sw = Scene();
        int removed = 0;
        if (sw != null)
            foreach (var pc in colliders)
            {
                var coll = (Collider*)pc;
                bool live = false;
                foreach (var c in sw->Scene->Colliders) if (c == coll) { live = true; break; }
                if (live) { sw->RemoveCollider(coll); removed++; }
            }
        log.Information("[ColliderSpawner] cleared " + removed + "/" + colliders.Count + " collider(s)");
        colliders.Clear();
    }

    public void Dispose() => Clear();
}
