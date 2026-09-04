namespace HMSync.Services;

using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System.Numerics;   // GetColor/SetColor vfuncs take System.Numerics.Vector4* (not the CS Common.Math one)
using HMSync.Sync;
using HMSync.Wire;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
// LightsOutService — "who lit the braziers?" atmosphere toggle for INSTANCED DUNGEONS, synced to peers.
//
// TWO independent map-global suppressions (per the product decisions in Q-0010), each a separate command:
//   • STAGE LIGHTS (Layer 0): the placed Light layout instances — the amber ambient glow a brazier/torch casts,
//     plus a dungeon's directional/point stage lights. Extinguished by SAVE-then-BLACK on the light's colour
//     (GetColor→cache→SetColor(black)); restored from the cache. NEVER SetActive/vf63 (crashes Deinit — the
//     S312 lesson). Lights are almost always nested inside SharedGroup prefabs, so we recurse SharedGroups
//     (the flat InstancesByType[Light] bucket is near-empty in a dungeon).
//   • ALL VFX (Layer 1): every VFX layout instance on the map — hidden by DrawObject.IsVisible=false, a leaf-state
//     accessor, never a lifecycle flag. Started life flame-only (a fire|flame|torch|… path-token allowlist) but was
//     made UNIVERSAL by request ("for a laugh") — a full atmosphere blackout, no filter. The associated-bloom
//     heuristic is FREE here: a brazier's slaved bloom light is PART of the VFX draw object, so IsVisible=false kills
//     the bloom with the sprite for every VFX that has one — nothing extra to do. This hides ENVIRONMENTAL layout VFX
//     only (weather, effect props, etc.); actor-attached VFX aren't LayoutWorld InstancesByType[Vfx] entries. It's a
//     restorable toggle, gated to instanced dungeons. FlameTokens survive only to TAG matches in the `vfxlist` dry
//     run — they no longer gate suppression.
//
// SYNC MODEL (mirrors OwnBodyHidden 0x53 / Freeze 0x55): a tiny intent bit rides lane 0x56 — NO light data on the
// wire. Each peer runs the SAME local suppression on its OWN layout copy. ANYONE in the session may toggle (no
// host gate). The receiver applies only when the payload's TerritoryId matches its own current zone (peers are
// co-located, so this is a co-map guard + late-join safety). Snapshot-able: re-offered on peer-join for newcomers.
//
// SCOPE: gated ONLY by "is an HMS map loaded" (zoneLoad.IsZoneLoaded). Works on ANY loaded map — city, overworld,
// dungeon — since a loaded map always has a layout to suppress. NOT filtered by territory type: the earlier
// instanced-content gate (ContentFinderCondition.Content != 0) was too narrow and refused VFX-heavy cities. No map
// loaded ⇒ no layout to touch ⇒ refuse.
//
// THREADING: /hmst command callbacks run on the framework thread → toggle methods apply directly. relay events
// fire on the receive thread → the receiver marshals onto the framework thread before touching the layout.
// Ported from a validated layout light/VFX walker (ForEachLight/WalkGroupLights/ApplyLightState/VfxSetVisible),
// minus its coupling/refcount/range machinery — HMS needs one global on/off per layer.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
public sealed unsafe class LightsOutService : IDisposable
{
    // Path tokens that flag a VFX as an obvious flame/fire prop. NO LONGER gates suppression (vfxoff is universal) —
    // used purely to TAG matches in the `vfxlist` dry run so the flame-y props stand out in the enumeration.
    private static readonly string[] FlameTokens =
        { "fire", "flame", "torch", "brazier", "candle", "ember", "campfire", "bonfire" };

    private readonly RelaySyncService relay;
    private readonly IFramework framework;
    // The EFFECTIVE territory: the map HMS actually streamed into the layout, NOT clientState.TerritoryType. In a
    // synthetic HMS session the game's TerritoryType stays pinned to the ORIGIN zone the player physically occupies
    // server-side (the city/overworld we loaded the dungeon over), while the rendered layout — and every light/VFX we
    // suppress — belongs to zoneLoad.CurrentLoadedZone. Supplied as `IsZoneLoaded ? CurrentLoadedZone :
    // clientState.TerritoryType`, so it's correct both in a synthetic session AND a genuine server-side dungeon. This
    // was the "reads the origin instead of the target TT" gate bug.
    private readonly Func<uint> loadedTerritory;
    // "Is an HMS virtual map loaded?" (zoneLoad.IsZoneLoaded). The ONLY gate now — the feature works on ANY loaded map
    // (city, overworld, dungeon), not just instanced content. No map ⇒ no layout to touch ⇒ refuse. Replaces the old
    // instanced-content (ContentFinderCondition) gate, which wrongly refused VFX-heavy cities.
    private readonly Func<bool> mapLoaded;
    private readonly IPluginLog log;
    private readonly Func<ulong> localContentId;
    private readonly Action<string> chatPrint;

    // ── Suppression STATE (framework-thread only) ── map-global, keyed to activeTerritory. Persists across a
    // leave/return so re-entering the same dungeon re-applies; a different map is a no-op until toggled there.
    private bool stageSuppressed;
    private bool vfxSuppressed;
    private uint activeTerritory;   // the TT the current suppression belongs to (0 = none)

    // Per-instance rollback state, keyed by ILayoutInstance.Id.InstanceKey. Cleared on a real zone change (the
    // instances are torn down; their keys are dead), so it is only ever consulted against a LIVE layout.
    private readonly Dictionary<uint, Vector4> savedLightColor = new();
    private readonly HashSet<uint> hiddenVfx = new();

    private uint lastTerritory;   // zone-change edge detector for the per-tick cache flush

    public LightsOutService(RelaySyncService relay, IFramework framework, Func<uint> loadedTerritory,
        Func<bool> mapLoaded, IPluginLog log, Func<ulong> localContentId, Action<string> chatPrint)
    {
        this.relay = relay;
        this.framework = framework;
        this.loadedTerritory = loadedTerritory;
        this.mapLoaded = mapLoaded;
        this.log = log;
        this.localContentId = localContentId;
        this.chatPrint = chatPrint;

        relay.OnLightsOutReceived += OnLightsOutReceived;
    }

    // ═══════════════════════════ COMMAND ENTRY POINTS (framework thread) ═══════════════════════════

    /// <summary>/hmst stagelights — toggle the placed stage/brazier LIGHTS (amber glow) map-wide, and broadcast.</summary>
    public void ToggleStageLights()
    {
        uint tt = loadedTerritory();
        if (!GateLoaded()) return;
        activeTerritory = tt;
        stageSuppressed = !stageSuppressed;
        ReassertLights();
        chatPrint("[HMSync] Stage lights " + (stageSuppressed ? "OUT" : "restored") + " for this map.");
        Broadcast(0, stageSuppressed, tt);
    }

    /// <summary>/hms vfxoff — toggle ALL VFX (every layout VFX + any slaved bloom) map-wide, and broadcast.</summary>
    public void ToggleVfx()
    {
        uint tt = loadedTerritory();
        if (!GateLoaded()) return;
        activeTerritory = tt;
        vfxSuppressed = !vfxSuppressed;
        ReassertVfx();
        chatPrint("[HMSync] All VFX " + (vfxSuppressed ? "OFF" : "restored") + " for this map.");
        Broadcast(1, vfxSuppressed, tt);
    }

    /// <summary>/hms vfxlist — DRY RUN: enumerate EVERY VFX on the map (path + key) without hiding anything, tagging the
    /// flame-token ones. Diagnostic to see exactly what `vfxoff` will blank out (and which are obvious fire props).</summary>
    public void DryRunVfx()
    {
        uint tt = loadedTerritory();
        int matched = 0, total = 0;
        ForEachVfx((inst, key, path) =>
        {
            total++;
            bool flame = IsFlamePath(path);
            if (flame) matched++;
            // Full list goes to the plugin log (filterable); chat only echoes the flame-y ones so it isn't spammed.
            log.Information("[LightsOut] VFX key=" + key + (flame ? " [flame]" : "") + " path=" + path);
            if (flame) chatPrint("[HMSync] flame VFX key=" + key + "  " + path);
        });
        chatPrint("[HMSync] vfxlist: " + total + " VFX total (" + matched + " flame-token) in TT " + tt +
                  (mapLoaded() ? " — full list in the log" : " (no map loaded)"));
    }

    // ═══════════════════════════ PER-TICK RE-ASSERT (framework thread) ═══════════════════════════
    // Called from the plugin's OnFrameworkUpdate. Idle-guarded no-op unless something is (or was) suppressed. Holds
    // the state against the game re-streaming a light/VFX (same reconcile discipline). On a real
    // zone change the layout is gone: flush the per-instance caches (their keys are dead) but KEEP the intent bits,
    // so returning to the same dungeon re-applies and a different map stays untouched.
    public void Tick()
    {
        uint tt = loadedTerritory();
        if (tt != lastTerritory)
        {
            savedLightColor.Clear();
            hiddenVfx.Clear();
            lastTerritory = tt;
        }
        // Nothing to do: no active intent AND no pending restore work.
        if (!stageSuppressed && !vfxSuppressed && savedLightColor.Count == 0 && hiddenVfx.Count == 0) return;
        // Suppression belongs to a different map (we left the dungeon it was set in) → hold, don't touch this layout.
        if (activeTerritory != 0 && tt != activeTerritory) return;
        if (!mapLoaded()) return;
        ReassertLights();
        ReassertVfx();
    }

    // ═══════════════════════════ WIRE COURIER (0x56) ═══════════════════════════

    private void Broadcast(byte layer, bool suppressed, uint tt)
    {
        if (!relay.IsConnected) return;
        _ = relay.SendLightsOut(new LightsOutPayload
        {
            SubjectId = "",
            SenderContentId = SelfCid(),
            TerritoryId = tt,
            Layer = layer,
            Suppressed = suppressed,
        });
    }

    /// <summary>Late-join re-offer: replay whatever suppression is currently active so a newcomer catches up. Called
    /// from OnPeerJoined alongside the other BroadcastFullState re-offers. Only re-sends ACTIVE suppressions (the
    /// default is lights-on; no need to spam "restored").</summary>
    public void BroadcastFullState()
    {
        if (!relay.IsConnected) return;
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (activeTerritory == 0) return;
            if (stageSuppressed) Broadcast(0, true, activeTerritory);
            if (vfxSuppressed) Broadcast(1, true, activeTerritory);
        });
    }

    private void OnLightsOutReceived(LightsOutPayload p)
    {
        if (p.SenderContentId == SelfCid()) return;   // never re-apply our own broadcast
        _ = framework.RunOnFrameworkThread(() =>
        {
            // STORE the intent keyed to the payload's territory (peers are co-located, so this is that dungeon).
            // We do NOT hard-drop on a TT mismatch: a late-joiner can receive the seed a frame before their own zone
            // finishes loading into the dungeon. Storing lets Tick apply it the moment their TT flips to activeTerritory.
            activeTerritory = p.TerritoryId;
            if (p.Layer == 0) stageSuppressed = p.Suppressed;
            else if (p.Layer == 1) vfxSuppressed = p.Suppressed;
            // Apply immediately IFF we're already standing in that dungeon; otherwise Tick applies it on arrival.
            uint tt = loadedTerritory();
            if (tt == p.TerritoryId && mapLoaded())
            {
                ReassertLights();
                ReassertVfx();
            }
            log.Information("[LightsOut] stored peer toggle layer=" + p.Layer +
                            " suppressed=" + p.Suppressed + " forTT=" + p.TerritoryId + " nowTT=" + tt);
        });
    }

    // ═══════════════════════════ LIGHT SUPPRESSION ═══════════════════════════════════════════════════

    private void ReassertLights()
    {
        ForEachLight((inst, key) =>
        {
            if (stageSuppressed)
            {
                if (!savedLightColor.ContainsKey(key))
                {
                    Vector4 cur = default; inst->GetColor(&cur); savedLightColor[key] = cur;
                }
                Vector4 black = default; inst->SetColor(&black);
            }
            else if (savedLightColor.TryGetValue(key, out var saved))
            {
                inst->SetColor(&saved);
                savedLightColor.Remove(key);
            }
        });
    }

    // Walk every Light in both the active and global layouts: the flat InstancesByType[Light] bucket AND every
    // (nested) SharedGroup, since dungeon brazier/torch lights live inside SharedGroup prefabs.
    private delegate void LightFn(ILayoutInstance* inst, uint key);

    private void ForEachLight(LightFn fn)
    {
        var world = LayoutWorld.Instance();
        if (world == null) return;
        WalkLightLayout(world->ActiveLayout, fn);
        if (world->GlobalLayout != world->ActiveLayout)
            WalkLightLayout(world->GlobalLayout, fn);
    }

    private void WalkLightLayout(LayoutManager* layout, LightFn fn)
    {
        if (layout == null) return;
        if (layout->InstancesByType.TryGetValuePointer(InstanceType.Light, out var m) && m != null && m->Value != null)
        {
            foreach (var kv in *m->Value)
            {
                var inst = kv.Item2.Value;
                if (inst == null) continue;
                fn(inst, inst->Id.InstanceKey);
            }
        }
        if (layout->InstancesByType.TryGetValuePointer(InstanceType.SharedGroup, out var sg) && sg != null && sg->Value != null)
        {
            foreach (var kv in *sg->Value)
            {
                var g = (SharedGroupLayoutInstance*)kv.Item2.Value;
                if (g == null) continue;
                WalkGroupLights(g, fn);
            }
        }
    }

    private void WalkGroupLights(SharedGroupLayoutInstance* sg, LightFn fn)
    {
        if (sg == null) return;
        foreach (var cptr in sg->Instances.Instances)
        {
            var child = cptr.Value;
            if (child == null) continue;
            var inst = child->Instance;
            if (inst == null) continue;
            var ty = inst->Id.Type;
            if (ty == InstanceType.Light) fn(inst, inst->Id.InstanceKey);
            else if (ty == InstanceType.SharedGroup) WalkGroupLights((SharedGroupLayoutInstance*)inst, fn);
        }
    }

    // ═══════════════════════════ FLAME VFX SUPPRESSION ═══════════════════════════

    private void ReassertVfx()
    {
        ForEachVfx((inst, key, path) =>
        {
            // Universal: no path filter — every VFX gets hidden (its slaved bloom, if any, dies with the draw object).
            if (vfxSuppressed)
            {
                var gfx = inst->GetGraphics();
                if (gfx != null) ((DrawObject*)gfx)->IsVisible = false;
                hiddenVfx.Add(key);
            }
            else if (hiddenVfx.Contains(key))
            {
                var gfx = inst->GetGraphics();
                if (gfx != null) ((DrawObject*)gfx)->IsVisible = true;
                hiddenVfx.Remove(key);
            }
        });
    }

    private delegate void VfxFn(ILayoutInstance* inst, uint key, string path);

    private void ForEachVfx(VfxFn fn)
    {
        var world = LayoutWorld.Instance();
        if (world == null) return;
        WalkVfxLayout(world->ActiveLayout, fn);
        if (world->GlobalLayout != world->ActiveLayout)
            WalkVfxLayout(world->GlobalLayout, fn);
    }

    private void WalkVfxLayout(LayoutManager* layout, VfxFn fn)
    {
        if (layout == null) return;
        if (layout->InstancesByType.TryGetValuePointer(InstanceType.Vfx, out var m) && m != null && m->Value != null)
        {
            foreach (var kv in *m->Value)
            {
                var inst = kv.Item2.Value;
                if (inst == null) continue;
                fn(inst, inst->Id.InstanceKey, PathOf(inst));
            }
        }
        // Brazier flame VFX are usually bundled INSIDE the SharedGroup prefab with the model + light, so recurse.
        if (layout->InstancesByType.TryGetValuePointer(InstanceType.SharedGroup, out var sg) && sg != null && sg->Value != null)
        {
            foreach (var kv in *sg->Value)
            {
                var g = (SharedGroupLayoutInstance*)kv.Item2.Value;
                if (g == null) continue;
                WalkGroupVfx(g, fn);
            }
        }
    }

    private void WalkGroupVfx(SharedGroupLayoutInstance* sg, VfxFn fn)
    {
        if (sg == null) return;
        foreach (var cptr in sg->Instances.Instances)
        {
            var child = cptr.Value;
            if (child == null) continue;
            var inst = child->Instance;
            if (inst == null) continue;
            var ty = inst->Id.Type;
            if (ty == InstanceType.Vfx) fn(inst, inst->Id.InstanceKey, PathOf(inst));
            else if (ty == InstanceType.SharedGroup) WalkGroupVfx((SharedGroupLayoutInstance*)inst, fn);
        }
    }

    private static string PathOf(ILayoutInstance* inst)
    {
        try { return inst->GetPrimaryPath().ToString() ?? ""; }
        catch { return ""; }
    }

    private static bool IsFlamePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var p = path.ToLowerInvariant();
        foreach (var tok in FlameTokens)
            if (p.Contains(tok)) return true;
        return false;
    }

    // ═══════════════════════════ GATES / HELPERS ═══════════════════════════

    private ulong SelfCid()
    {
        try { return localContentId(); } catch { return 0; }
    }

    // Map-loaded gate WITH a user-facing refusal (command path). The feature works on ANY loaded HMS map — the guard is
    // simply "is a map loaded", not a territory-type filter (a city full of VFX is as valid a target as a dungeon).
    private bool GateLoaded()
    {
        if (mapLoaded()) return true;
        chatPrint("[HMSync] Lights-out only works while a map is loaded.");
        return false;
    }

    /// <summary>Restore everything and forget all state (session reset / plugin unload). Walks the LIVE layout so
    /// colours/visibility snap back; leftover keys for gone instances are just dropped.</summary>
    public void RestoreAll()
    {
        stageSuppressed = false;
        vfxSuppressed = false;
        try { ReassertLights(); } catch { }
        try { ReassertVfx(); } catch { }
        savedLightColor.Clear();
        hiddenVfx.Clear();
        activeTerritory = 0;
    }

    public void Dispose()
    {
        try { relay.OnLightsOutReceived -= OnLightsOutReceived; } catch { }
        try { RestoreAll(); } catch { }
    }
}
