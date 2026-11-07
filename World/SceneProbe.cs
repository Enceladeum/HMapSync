using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace HMSync.World;

// ============================================================================
// SceneProbe - read-only dump of the drawn background models in the render scene, with their LIVE transform.
//
// WORLD-LAYER PRIMITIVE (no HMS dependencies). Built to read what the engine actually renders during a cutscene
// replay (the desk-scale question, b267): the file data says scale 1 for the Valens desks, the footage says ~1.3, so
// measure the running scene instead of guessing which field carries it. Walks World's child objects (and one level of
// their children), keeps BgObjects whose model path contains the filter, logs path / position / rotation / scale.
// Writes nothing. Framework thread only.
// ============================================================================
public static unsafe class SceneProbe
{
    private const int MaxObjects = 20000;

    public static int Dump(IPluginLog log, string pathFilter)
    {
        var world = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.World.Instance();
        if (world == null) { log.Information("[SCENEPROBE] no World"); return 0; }
        int seen = 0, hits = 0;
        foreach (var top in ((SceneObject*)world)->ChildObjects)
        {
            if (++seen > MaxObjects) break;
            hits += Visit(log, top, pathFilter);
            foreach (var child in top->ChildObjects)
            {
                if (++seen > MaxObjects) break;
                hits += Visit(log, child, pathFilter);
            }
        }
        log.Information($"[SCENEPROBE] '{pathFilter}': {hits} match(es) among {seen} scene object(s)");
        return hits;
    }

    private static int Visit(IPluginLog log, SceneObject* o, string filter)
    {
        if (o == null || o->GetObjectType() != ObjectType.BgObject) return 0;
        var bg = (BgObject*)o;
        if (bg->ModelResourceHandle == null) return 0;
        string path;
        try { path = ((ResourceHandle*)bg->ModelResourceHandle)->FileName.ToString(); }
        catch { return 0; }
        if (filter.Length > 0 && path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) return 0;
        var p = o->Position; var r = o->Rotation; var s = o->Scale;
        log.Information($"[SCENEPROBE] {path} pos=({p.X:F3},{p.Y:F3},{p.Z:F3}) rotQ=({r.X:F3},{r.Y:F3},{r.Z:F3},{r.W:F3}) " +
                        $"scale=({s.X:F3},{s.Y:F3},{s.Z:F3}) visible={((DrawObject*)o)->IsVisible} obj=0x{(nint)o:X}");
        return 1;
    }
}
