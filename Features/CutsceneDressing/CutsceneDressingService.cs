using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using HMSync.World;

namespace HMSync.Features.CutsceneDressing;

// ============================================================================
// CutsceneDressingService - layers a cutscene's set dressing onto a loaded map: hides the level instances the scene
// switches off (its cutb instance-reference table), then places the scene's own props.
//
// Feature module, deliberately self-contained (no reach into ZoneLoadService / HMSyncPlugin internals): it gets a
// "map settled" predicate and is driven by four calls from the host plugin, so it can move or be deleted whole.
//   Arm(row)          before a load: the NEXT zone change carries this dressing (0 = none)
//   Disarm()          after the load call returns: drops an arm the load never consumed (aborted load)
//   OnZoneWillChange  subscribed to ZoneLoadService.ZoneWillChange: tears the current dressing down BEFORE the
//                     scene unloads (BgSpawner rule 3), then promotes the armed row to active
//   Clear()           explicit teardown for paths that bypass ZoneWillChange (session revert) and dispose
// Tick() lands the dressing once the map has settled. Peers apply the same row locally: the data is
// deterministic, so the wire carries only the row id (ZoneLoadData "csr").
//
// Data: DressingCatalog (b272: derived at runtime from the installed game by DressingIngest, once per game version;
// the embedded copy is the bootstrap). Engine work: World.BgSpawner +
// World.LayoutInstanceVisibility.
// ============================================================================
public sealed class CutsceneDressingService : IDisposable
{
    private const int SettleFrames = 90;        // wait after the map reports settled before spawning (streaming)
    private const int HidePassEvery = 30;       // re-run the hide pass for as long as the dressing is active: parts stream
                                                // in as you move on big maps, and the game may re-show some
    private const int HideReportAt = 600;       // log one tally ~10s in
    private const int ReplaceScanEvery = 300;   // b272: live scan for the level's own copies of re-placed props (~5s; streams in)
    private const float ReplaceRadius = 0.6f;

    private readonly IPluginLog log;
    private readonly IDataManager data;
    private readonly Func<bool> mapSettled;
    private readonly BgSpawner spawner;
    private readonly LayoutInstanceVisibility hider = new();
    private readonly ColliderSpawner colliders;   // b269: the props' own collision meshes

    private uint armedRow;
    private Dressing? active;
    // What the active dressing switches off: its baked hides, or (lab entries) read from the cutb at activation.
    private Dictionary<InstanceType, HashSet<ulong>> activeHides = new();   // own copy per activation (b272 adds to it live)
    // Lights only, re-asserted EVERY frame: the game re-lights between 30-frame passes (b262 flicker, Valens).
    private IReadOnlyDictionary<InstanceType, HashSet<ulong>> activeLights = new Dictionary<InstanceType, HashSet<ulong>>();
    private int activeHideCount;
    private int settledFrames;
    private bool propsPlaced;
    private int hideFrames;
    private LayoutInstanceVisibility.Result lastHide;

    /// <summary>
    /// Euler -> quaternion convention for props, switchable in-game (/hms dressing rot N) until one is confirmed:
    /// 0 = ClientStructs Quaternion.FromEulerRad (qZ*qY*qX: X, then Y, then Z) - the default, hangar-consistent;
    /// 1 = the measured native-collider rule, CreateFromYawPitchRoll(-Y, X, Z) (yaw sign negated);
    /// 2 = vnavmesh's LGB rule, CreateFromYawPitchRoll(Y, X, Z) (Z applied first).
    /// </summary>
    public int RotMode { get; set; }

    public const int RotModeCount = 3;

    // b266 diagnostic: live per-prop experiments (path substring -> size multiplier, vertical offset), applied on (re)spawn.
    private readonly Dictionary<string, (float Scale, float Dy)> tweaks = new(StringComparer.OrdinalIgnoreCase);

    public void SetTweak(string pathSubstring, float scale, float dy)
    {
        var cur = tweaks.TryGetValue(pathSubstring, out var t) ? t : (1f, 0f);
        tweaks[pathSubstring] = (scale != 1f ? scale : cur.Item1, dy != 0f ? dy : cur.Item2);
    }

    public void ClearTweaks() => tweaks.Clear();

    /// <summary>The live catalog. Starts as the shipped (embedded) data; b272 swaps in the runtime ingest for the
    /// installed game version once it is ready (ReplaceCatalog, framework thread).</summary>
    public DressingCatalog Catalog { get; private set; }

    /// <summary>b272: adopt a freshly ingested catalog. The active dressing (already resolved) keeps running; an armed
    /// row missing from the new catalog is dropped.</summary>
    public void ReplaceCatalog(DressingCatalog next)
    {
        Catalog = next;
        if (armedRow != 0 && !next.TryGet(armedRow, out _)) armedRow = 0;
    }

    /// <summary>Row of the dressing on the current map (0 = none). What the host broadcasts.</summary>
    public uint ActiveRow => active?.Row ?? 0;

    public string? ActiveName => active?.Name;

    public CutsceneDressingService(IPluginLog log, ISigScanner sigScanner, IDataManager data, Func<bool> mapSettled)
    {
        this.log = log;
        this.data = data;
        this.mapSettled = mapSettled;
        Catalog = new DressingCatalog(log);
        spawner = new BgSpawner(log, sigScanner, "hms_dressing");
        colliders = new ColliderSpawner(log);
    }

    public void Arm(uint row)
    {
        if (row != 0 && !Catalog.TryGet(row, out _)) { log.Warning("[Dressing] arm: unknown row " + row + ", ignoring"); row = 0; }
        armedRow = row;
    }

    public void Disarm() => armedRow = 0;

    /// <summary>The armed dressing's own arrival point, if it has one (big field maps). Read by the load path before
    /// the zone changes, so it ranks above the map's default spawn.</summary>
    public bool TryGetArmedSpawn(out Vector3 spawn)
    {
        spawn = default;
        if (armedRow == 0 || !Catalog.TryGet(armedRow, out var d) || d.Spawn is not { } s) return false;
        spawn = s;
        return true;
    }

    /// <summary>ZoneWillChange handler: tear down the current dressing, then make the armed one (if any) active.</summary>
    public void OnZoneWillChange()
    {
        Teardown();
        if (armedRow != 0 && Catalog.TryGet(armedRow, out var d))
        {
            Activate(d);
            log.Information("[Dressing] '" + d.Name + "' (row " + d.Row + ") will apply after the load settles: " +
                d.Props.Count + " prop(s); switches off " + string.Join(", ", activeHides.Select(kv => kv.Value.Count + " " + kv.Key)) +
                (d.HidesFromCutb ? " (read from the cutb)" : "") +
                (d.HideOther.Count > 0 ? " (not handled: " + string.Join(", ", d.HideOther.Select(kv => kv.Value + " of type " + kv.Key)) + ")" : ""));
        }
        armedRow = 0;
    }

    private void Activate(Dressing d)
    {
        active = d;
        activeHides = Merge(d.HidesFromCutb ? CutbInstanceRefs.Read(data, d.Cutb) : new(), d.Hides);   // b264: cutb list + baked hides
        activeLights = activeHides.TryGetValue(InstanceType.Light, out var l)
            ? new Dictionary<InstanceType, HashSet<ulong>> { [InstanceType.Light] = l }
            : new Dictionary<InstanceType, HashSet<ulong>>();
        activeHideCount = activeHides.Values.Sum(v => v.Count);
    }

    private static Dictionary<InstanceType, HashSet<ulong>> Merge(Dictionary<InstanceType, HashSet<ulong>> into, IReadOnlyDictionary<InstanceType, HashSet<ulong>> add)
    {
        foreach (var (type, keys) in add)
        {
            if (!into.TryGetValue(type, out var set)) into[type] = set = new HashSet<ulong>();
            set.UnionWith(keys);
        }
        return into;
    }

    /// <summary>Explicit teardown (session revert / dispose): nothing stays active.</summary>
    public void Clear() => Teardown();

    /// <summary>Tear down and re-place the active dressing on the current map (diagnostic: e.g. after changing RotMode).</summary>
    public void Reapply()
    {
        var d = active;
        Teardown();
        if (d != null) Activate(d);
    }

    private void Teardown()
    {
        spawner.Clear();
        colliders.Clear();   // b269: before the zone unloads, like the models
        hider.Restore();
        active = null;
        activeHides = new();
        activeLights = new Dictionary<InstanceType, HashSet<ulong>>();
        activeHideCount = 0;
        settledFrames = 0;
        propsPlaced = false;
        hideFrames = 0;
        lastHide = default;
    }

    public void Tick()
    {
        spawner.Tick();
        var d = active;
        if (d == null) return;

        if (!mapSettled()) { settledFrames = 0; return; }
        if (settledFrames < SettleFrames) { settledFrames++; return; }

        // Hide first: the scene's props replace the level's own copies (the default bed, Rabanastre's ruins), so the
        // originals must be gone before the replacements land.
        // b272: the level's own copy of a re-placed prop (same model within ReplaceRadius) is found live, not baked.
        if (d.Props.Count > 0 && hideFrames % ReplaceScanEvery == 0) AddReplacementHides(d);
        if (activeHideCount > 0 && hideFrames % HidePassEvery == 0) lastHide = hider.Apply(activeHides);
        else if (activeLights.Count > 0) hider.Apply(activeLights);
        if (hideFrames == HideReportAt && activeHideCount > 0)
            log.Information("[Dressing] '" + d.Name + "' hides: " + hider.HiddenCount + "/" + activeHideCount + " switched off by us (live now: " +
                lastHide.Matched + " matched); the rest are not streamed in here yet, or were already off");
        hideFrames++;

        if (!propsPlaced)
        {
            propsPlaced = true;
            int ok = 0, solid = 0;
            foreach (var p in d.Props)
            {
                var pos = p.Pos; var scale = p.Scale;
                foreach (var (sub, t) in tweaks)
                    if (p.Path.Contains(sub, StringComparison.OrdinalIgnoreCase)) { scale *= t.Scale; pos.Y += t.Dy; }
                if (spawner.Spawn(p.Path, pos, ToQuaternion(p.RotEuler, RotMode), scale)) ok++;
                if (p.Coll != null && colliders.Add(p.Coll, pos, p.RotEuler, scale)) solid++;   // b269: same transform as the model
            }
            log.Information("[Dressing] '" + d.Name + "': spawned " + ok + "/" + d.Props.Count + " prop(s), " + solid + " with collision" +
                (spawner.GroupsAvailable ? "" : " (.sgb path unavailable, groups skipped)"));
        }
    }

    private void AddReplacementHides(Dressing d)
    {
        var targets = new List<(string, Vector3)>(d.Props.Count * 2);
        foreach (var p in d.Props)
        {
            targets.Add((p.Path, p.Pos));
            int a = p.Path.IndexOf("/asset/", StringComparison.Ordinal);   // housing swap: the level copy is the .mdl
            if (a > 0 && p.Path.EndsWith(".sgb", StringComparison.Ordinal))
                targets.Add((p.Path[..a] + "/bgparts/" + p.Path[(a + 7)..^4] + ".mdl", p.Pos));
        }
        var found = LayoutInstanceVisibility.BgPartsNear(targets, ReplaceRadius);
        if (found.Count == 0) return;
        if (!activeHides.TryGetValue(InstanceType.BgPart, out var set)) activeHides[InstanceType.BgPart] = set = new HashSet<ulong>();
        int before = set.Count;
        set.UnionWith(found);
        if (set.Count == before) return;
        activeHideCount = activeHides.Values.Sum(v => v.Count);   // ReplaceScanEvery is a multiple of HidePassEvery: applied this frame
        log.Information("[Dressing] '" + d.Name + "': " + (set.Count - before) + " level copy(ies) of re-placed props found live, switched off");
    }

    public string Status()
    {
        var d = active;
        if (d == null) return "no dressing active" + (armedRow != 0 ? " (armed row " + armedRow + ")" : "");
        return "'" + d.Name + "' row " + d.Row + ": props " + spawner.LandedCount + "/" + spawner.Count + " landed of " + d.Props.Count +
            ", " + colliders.Count + " collider(s)" +
            "; switched off " + hider.HiddenCount + "/" + activeHideCount + " level instance(s)" +
            (propsPlaced ? "" : " (waiting for map to settle)") + " [rot " + RotMode + "]" +
            " [catalog: " + Catalog.Source + "]" +
            (tweaks.Count > 0 ? " tweaks: " + string.Join(", ", tweaks.Select(kv => kv.Key + " x" + kv.Value.Scale + " dy" + kv.Value.Dy)) : "");
    }

    // See RotMode. Mode 0 builds Rx*Ry*Rz as row-vector matrices (X applied first), identical to ClientStructs'
    // FromEulerRad (qZ*qY*qX); building the matrix avoids hand-deriving the quaternion product order.
    private static Quaternion ToQuaternion(Vector3 e, int mode) => mode switch
    {
        1 => Quaternion.CreateFromYawPitchRoll(-e.Y, e.X, e.Z),
        2 => Quaternion.CreateFromYawPitchRoll(e.Y, e.X, e.Z),
        _ => Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(
                 Matrix4x4.CreateRotationX(e.X) * Matrix4x4.CreateRotationY(e.Y) * Matrix4x4.CreateRotationZ(e.Z))),
    };

    public void Dispose()
    {
        Teardown();
        spawner.Dispose();
        colliders.Dispose();
    }
}
