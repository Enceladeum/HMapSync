using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using Vector4 = System.Numerics.Vector4;   // GetColor/SetColor take System.Numerics.Vector4* (as LightsOutService)

namespace HMSync.World;

// ============================================================================
// LayoutInstanceVisibility - switch specific layout instances of the loaded level OFF by (layer, instance) key, and
// put back exactly what it switched off.
//
// WORLD-LAYER PRIMITIVE (no HMS dependencies; graduates to Enceladeum.World with BgSpawner).
// Keys are what the runtime carries: ILayoutInstance.Id.LayerKey (low 16 bits of the LGB layer id) + Id.InstanceKey.
// Cutscene instance-reference tables name instances the same way. Top-level instances only (SubId == 0).
//
// "Off" per type, all levers already proven elsewhere in HMS:
//   BgPart      graphics IsVisible=false (the GraphicsObject FIELD, S182) + SetColliderActive(false) (vf37, as
//               ZoneLoadService's spawn-ring/barrier suppression), so a hidden object stops blocking you too
//   Light       GetColor -> cache -> SetColor(black) (LightsOutService's lever; NEVER SetActive/vf63)
//   Vfx         GetGraphics() IsVisible=false (LightsOutService's VFX lever)
//   SharedGroup SetColliderActive(false) on the group + every child part/VFX hidden and child light blacked
//
// Records KEYS, never pointers (big maps stream instances in and out; a stored pointer can be freed), and only what it
// actually flipped: an instance the game already had hidden is left alone and is not "restored" to visible. Apply()
// is re-run periodically (catches newly streamed instances and anything the game re-shows). Restore() re-walks live
// instances and undoes only its own changes; it must run BEFORE the zone unloads. Framework thread only.
// ============================================================================
public sealed unsafe class LayoutInstanceVisibility
{
    public readonly record struct Result(int Matched, int Flipped);

    // Keys this instance switched off, per type. Lights keep their original colour for the restore.
    private readonly HashSet<ulong> parts = new();
    private readonly HashSet<ulong> vfx = new();
    private readonly HashSet<ulong> groups = new();
    private readonly Dictionary<ulong, Vector4> lights = new();
    private readonly Dictionary<(ulong Group, uint Child), Vector4> groupLights = new();

    public int HiddenCount => parts.Count + vfx.Count + groups.Count + lights.Count;

    // b265: instance id only. Cutscene reference tables can name an instance under a DIFFERENT layer than the one that
    // owns it (z3e4: the blue back-wall device obj01 is filed under the room layer 0x1E17 but referenced as 0x1E19), so
    // a layer-qualified key silently missed them. Instance ids are unique within a level for practical purposes (~7.2M
    // range in z3e4); the layer argument is kept so callers stay unchanged.
    public static ulong Key(ushort layerKey, uint instanceKey) => instanceKey;

    /// <summary>Switch off every live instance named in <paramref name="byType"/>. Idempotent; call periodically.</summary>
    public Result Apply(IReadOnlyDictionary<InstanceType, HashSet<ulong>> byType)
    {
        int matched = 0, flipped = 0;
        foreach (var (type, keys) in byType)
        {
            if (keys.Count == 0) continue;
            Walk(type, keys, (key, inst) =>
            {
                matched++;
                switch (type)
                {
                    case InstanceType.BgPart:
                    {
                        var go = ((BgPartsLayoutInstance*)inst)->GraphicsObject;   // the FIELD, not vf23 (S182)
                        if (go == null || !((DrawObject*)go)->IsVisible) return;   // not drawn yet, or already off (ours or the game's)
                        ((DrawObject*)go)->IsVisible = false;
                        inst->SetColliderActive(false);
                        parts.Add(key); flipped++;
                        break;
                    }
                    case InstanceType.Vfx:
                    {
                        var g = (DrawObject*)inst->GetGraphics();
                        if (g == null || !g->IsVisible) return;
                        g->IsVisible = false;
                        vfx.Add(key); flipped++;
                        break;
                    }
                    case InstanceType.Light:
                    {
                        if (!lights.ContainsKey(key)) { Vector4 cur = default; inst->GetColor(&cur); lights[key] = cur; flipped++; }
                        Vector4 black = default; inst->SetColor(&black);   // re-asserted each pass (lights re-stream)
                        break;
                    }
                    case InstanceType.SharedGroup:
                    {
                        if (groups.Add(key)) { inst->SetColliderActive(false); flipped++; }
                        SetGroup((SharedGroupLayoutInstance*)inst, key, false);
                        break;
                    }
                }
            });
        }
        return new Result(matched, flipped);
    }

    /// <summary>
    /// Keys of live, top-level BgParts drawing the same model as one of <paramref name="targets"/> within
    /// <paramref name="radius"/> of it: the level's own copy of an object a scene re-places (the default bed, a desk set
    /// moved half a yalm). Read-only; feed the result to Apply. Paths compare case-insensitively.
    /// </summary>
    public static HashSet<ulong> BgPartsNear(IReadOnlyList<(string Path, System.Numerics.Vector3 Pos)> targets, float radius)
    {
        var found = new HashSet<ulong>();
        if (targets.Count == 0) return found;
        var byPath = new Dictionary<string, List<System.Numerics.Vector3>>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (p, pos) in targets)
        {
            if (!byPath.TryGetValue(p, out var l)) byPath[p] = l = new();
            l.Add(pos);
        }
        var lw = LayoutWorld.Instance();
        if (lw == null) return found;
        float r2 = radius * radius;
        foreach (var layout in new[] { lw->ActiveLayout, lw->GlobalLayout })
        {
            if (layout == null) continue;
            if (!layout->InstancesByType.TryGetValuePointer(InstanceType.BgPart, out var m) || m == null || m->Value == null) continue;
            foreach (var kv in *m->Value)
            {
                var inst = kv.Item2.Value;
                if (inst == null || inst->SubId != 0) continue;
                string? path = null;
                try { var cs = inst->GetPrimaryPath(); if (cs.HasValue) path = cs.ToString(); } catch { }
                if (path == null || !byPath.TryGetValue(path, out var spots)) continue;
                System.Numerics.Vector3 ip; inst->GetTranslation(&ip);
                foreach (var s in spots)
                    if (System.Numerics.Vector3.DistanceSquared(s, ip) <= r2) { found.Add(Key(inst->Id.LayerKey, inst->Id.InstanceKey)); break; }
            }
        }
        return found;
    }

    /// <summary>Undo everything this switched off. Call before the zone unloads.</summary>
    public void Restore()
    {
        if (parts.Count > 0)
            Walk(InstanceType.BgPart, parts, (key, inst) =>
            {
                var go = ((BgPartsLayoutInstance*)inst)->GraphicsObject;
                if (go != null) ((DrawObject*)go)->IsVisible = true;
                inst->SetColliderActive(true);
            });
        if (vfx.Count > 0)
            Walk(InstanceType.Vfx, vfx, (key, inst) => { var g = (DrawObject*)inst->GetGraphics(); if (g != null) g->IsVisible = true; });
        if (lights.Count > 0)
            Walk(InstanceType.Light, new HashSet<ulong>(lights.Keys), (key, inst) => { var c = lights[key]; inst->SetColor(&c); });
        if (groups.Count > 0)
            Walk(InstanceType.SharedGroup, groups, (key, inst) =>
            {
                SetGroup((SharedGroupLayoutInstance*)inst, key, true);
                inst->SetColliderActive(true);
            });
        Forget();
    }

    /// <summary>Drop the records without touching memory (the scene is already gone).</summary>
    public void Forget()
    {
        parts.Clear(); vfx.Clear(); groups.Clear(); lights.Clear(); groupLights.Clear();
    }

    // Children of a group: parts and VFX visibility, lights blacked/restored (colour cached per group+child).
    private void SetGroup(SharedGroupLayoutInstance* sg, ulong groupKey, bool on)
    {
        if (sg == null) return;
        foreach (var cptr in sg->Instances.Instances)
        {
            var child = cptr.Value;
            if (child == null) continue;
            var inst = child->Instance;
            if (inst == null) continue;
            switch (inst->Id.Type)
            {
                case InstanceType.BgPart:
                {
                    var go = ((BgPartsLayoutInstance*)inst)->GraphicsObject;
                    if (go != null) ((DrawObject*)go)->IsVisible = on;
                    break;
                }
                case InstanceType.Vfx:
                {
                    var g = (DrawObject*)inst->GetGraphics();
                    if (g != null) g->IsVisible = on;
                    break;
                }
                case InstanceType.Light:
                {
                    var ck = (groupKey, inst->Id.InstanceKey);
                    if (on) { if (groupLights.Remove(ck, out var c)) inst->SetColor(&c); }
                    else
                    {
                        if (!groupLights.ContainsKey(ck)) { Vector4 cur = default; inst->GetColor(&cur); groupLights[ck] = cur; }
                        Vector4 black = default; inst->SetColor(&black);
                    }
                    break;
                }
                case InstanceType.SharedGroup:
                    SetGroup((SharedGroupLayoutInstance*)inst, groupKey, on);
                    break;
            }
        }
    }

    private delegate void Visit(ulong key, ILayoutInstance* inst);

    private static void Walk(InstanceType type, IReadOnlySet<ulong> keys, Visit visit)
    {
        var lw = LayoutWorld.Instance();
        if (lw == null) return;
        foreach (var layout in new[] { lw->ActiveLayout, lw->GlobalLayout })
        {
            if (layout == null) continue;
            if (!layout->InstancesByType.TryGetValuePointer(type, out var m) || m == null || m->Value == null) continue;
            foreach (var kv in *m->Value)
            {
                var inst = kv.Item2.Value;
                if (inst == null || inst->SubId != 0) continue;
                var key = Key(inst->Id.LayerKey, inst->Id.InstanceKey);
                if (keys.Contains(key)) visit(key, inst);
            }
        }
    }
}
