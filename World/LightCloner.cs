using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using SceneLight = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Light;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace HMSync.World;

// ============================================================================
// LightCloner - copies live level lights (layout Light instances) to new positions, and owns the copies until Clear().
//
// WORLD-LAYER PRIMITIVE (no HMS dependencies; graduates to Enceladeum.World with BgSpawner / ColliderSpawner).
// Brio's clone recipe (LightingService.Clone): Scene.Light.Create(shape, null) -> point the render light at the new
// object's own Transform -> copy the source render light's settings -> UpdateTransforms/UpdateCulling/UpdateMaterials.
// Unlike Brio we also copy MaxRange, so the copy is clipped to the same box as the original (a house light must not leak
// through the walls). The source is the level's own light, read live, so colour/intensity/range are exactly the game's.
//   source  : LayoutWorld InstancesByType[Light] (top-level, by InstanceKey); its Scene.Light* sits at +0x30 of the
//             layout instance (Meddle LightLayoutInstance.LightPtr)
//   teardown: hide, CleanupRender, Dtor(1) - the BgSpawner ladder - BEFORE the zone unloads
// Framework thread only.
// ============================================================================
public sealed unsafe class LightCloner : IDisposable
{
    private readonly IPluginLog log;
    private readonly List<nint> lights = new();
    private readonly byte[] pool;

    public int Count => lights.Count;

    public LightCloner(IPluginLog log, string poolName)
    {
        this.log = log;
        pool = System.Text.Encoding.UTF8.GetBytes(poolName + "\0");
    }

    /// <summary>Live level lights by instance key (top-level only), for the requested keys.</summary>
    public static Dictionary<uint, nint> FindSources(IReadOnlySet<uint> keys)
    {
        var found = new Dictionary<uint, nint>();
        var lw = LayoutWorld.Instance();
        if (lw == null || keys.Count == 0) return found;
        foreach (var layout in new[] { lw->ActiveLayout, lw->GlobalLayout })
        {
            if (layout == null) continue;
            if (!layout->InstancesByType.TryGetValuePointer(InstanceType.Light, out var m) || m == null || m->Value == null) continue;
            foreach (var kv in *m->Value)
            {
                var inst = kv.Item2.Value;
                if (inst == null || inst->SubId != 0 || !keys.Contains(inst->Id.InstanceKey)) continue;
                var light = *(SceneLight**)((byte*)inst + 0x30);
                if (light != null && light->RenderLight != null) found[inst->Id.InstanceKey] = (nint)light;
            }
        }
        return found;
    }

    /// <summary>Create a copy of a live light, moved by <paramref name="offset"/> and then turned by <paramref name="yaw"/>
    /// (radians, about Y) around <paramref name="pivot"/>. False if the engine refused it.</summary>
    public bool Clone(nint sourcePtr, Vector3 offset, float yaw = 0, Vector3 pivot = default)
    {
        var src = (SceneLight*)sourcePtr;
        if (src == null || src->RenderLight == null) return false;
        SceneLight* l;
        try
        {
            fixed (byte* p = pool) l = SceneLight.Create(src->RenderLight->LightShape, p, null);
        }
        catch (Exception ex) { log.Warning("[LightCloner] Light.Create threw: " + ex.Message); return false; }
        if (l == null || l->RenderLight == null) return false;
        try
        {
            var so = (SceneObject*)l;
            var ss = (SceneObject*)src;
            System.Numerics.Vector3 sp = ss->Position;
            System.Numerics.Quaternion sr = ss->Rotation;
            var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
            var moved = sp + offset;
            so->Position = yaw == 0 ? moved : pivot + Vector3.Transform(moved - pivot, turn);
            so->Rotation = yaw == 0 ? sr : Quaternion.Normalize(Quaternion.Concatenate(sr, turn));
            so->Scale = ss->Scale;
            var r = l->RenderLight; var s = src->RenderLight;
            // render light reads the object's own transform (Position/Rotation/Scale laid out from +0x50, as Brio's BrioLight.Transform)
            r->Transform = (FFXIVClientStructs.FFXIV.Client.Graphics.Transform*)((byte*)l + 0x50);
            r->LightFlags = s->LightFlags;
            r->LightShape = s->LightShape;
            r->Color = s->Color;
            r->Intensity = s->Intensity;
            r->MaxRange = s->MaxRange;
            r->ShadowPlaneNear = s->ShadowPlaneNear;
            r->ShadowPlaneFar = s->ShadowPlaneFar;
            r->FalloffType = s->FalloffType;
            r->FlatLightSkewAngleDegrees = s->FlatLightSkewAngleDegrees;
            r->FalloffFactor = s->FalloffFactor;
            r->SpotLightAngleDegrees = s->SpotLightAngleDegrees;
            r->AngularFalloffDegrees = s->AngularFalloffDegrees;
            r->Range = s->Range;
            r->CharacterShadowRange = s->CharacterShadowRange;
            var d = (DrawObject*)l;
            d->UpdateTransforms(false);
            d->UpdateCulling();
            d->UpdateMaterials();
            d->IsVisible = true;
        }
        catch (Exception ex) { log.Warning("[LightCloner] configure threw: " + ex.Message); }
        lights.Add((nint)l);
        return true;
    }

    public void Clear()
    {
        if (lights.Count == 0) return;
        foreach (var p in lights)
        {
            try
            {
                var d = (DrawObject*)p;
                d->IsVisible = false;
                d->CleanupRender();
                ((SceneObject*)p)->Dtor(1);
            }
            catch (Exception ex) { log.Warning("[LightCloner] teardown threw: " + ex.Message); }
        }
        log.Information("[LightCloner] cleared " + lights.Count + " light(s)");
        lights.Clear();
    }

    public void Dispose() => Clear();
}
