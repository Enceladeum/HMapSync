using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace HMSync.Services;

// Resolves inbound (ServerZone) / outbound (ClientZone) opcode → packet NAME for the packet inspector. Opcodes rotate
// per patch, so this is a best-effort LABEL only: it cannot affect behaviour (the firewall never consults it). The NAME
// is the durable identity we care about; the numbers are swappable.
//
// Two sources, in order of preference:
//   1) A live community-maintained map fetched once per session from karashiiro/FFXIVOpcodes on GitHub
//      (raw.githubusercontent.com — a public CDN, no auth, separate from the game network). Best-effort + async.
//   2) The EMBEDDED opcodes.min.json shipped in the assembly — the guaranteed baseline if the fetch is off/slow/failed.
// The embedded map always loads synchronously first so labels work instantly; a successful fetch quietly swaps in a
// fresher table. If the fetch can't reach GitHub we keep the embedded map and flag it as possibly stale.
public sealed class OpcodeMapService
{
    // karashiiro's map is the same shape as our embedded file (array; the {region:"Global"} block carries version+lists).
    private const string RemoteUrl = "https://raw.githubusercontent.com/karashiiro/FFXIVOpcodes/master/opcodes.min.json";

    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(6) };

    private readonly IPluginLog log;
    private readonly object gate = new();

    private Dictionary<ushort, string> serverZone = new();   // inbound
    private Dictionary<ushort, string> clientZone = new();   // outbound
    private string mapVersion = "unknown";
    private MapSource source = MapSource.Embedded;
    private bool refreshDone;
    private string liveGameVersion = "";

    private enum MapSource { Embedded, Remote, RemoteFailed }

    public string MapVersion { get { lock (gate) return mapVersion; } }

    public OpcodeMapService(IPluginLog log)
    {
        this.log = log;
        LoadEmbedded();
    }

    private void LoadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            // Embedded resource name is "<RootNamespace>.<file>" → "HMSync.opcodes.min.json".
            var resName = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith("opcodes.min.json", StringComparison.OrdinalIgnoreCase));
            if (resName == null) { log.Warning("[HMSync] opcode map resource not found"); return; }
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream == null) { log.Warning("[HMSync] opcode map stream null"); return; }
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            if (Parse(json, out var sz, out var cz, out var ver))
            {
                lock (gate) { serverZone = sz; clientZone = cz; mapVersion = ver; source = MapSource.Embedded; }
                log.Information("[HMSync] opcode map loaded (bundled): Global " + ver + " (" + sz.Count + " inbound, " + cz.Count + " outbound)");
            }
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] opcode map load failed: " + ex.Message);
        }
    }

    // Kick off the once-per-session best-effort refresh. Non-blocking; caller passes the live game version purely so the
    // status line can show it for context (the map version is a patch label, not comparable to the client's date stamp).
    public void StartRefresh(string gameVersion)
    {
        lock (gate)
        {
            if (refreshDone) return;
            refreshDone = true;
            liveGameVersion = gameVersion ?? "";
        }
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var json = await http.GetStringAsync(RemoteUrl).ConfigureAwait(false);
            if (Parse(json, out var sz, out var cz, out var ver) && sz.Count > 0)
            {
                lock (gate) { serverZone = sz; clientZone = cz; mapVersion = ver; source = MapSource.Remote; }
                log.Information("[HMSync] opcode map refreshed from GitHub: Global " + ver + " (" + sz.Count + " inbound, " + cz.Count + " outbound)");
                return;
            }
            lock (gate) source = MapSource.RemoteFailed;
            log.Warning("[HMSync] opcode map GitHub fetch parsed empty; keeping bundled map.");
        }
        catch (Exception ex)
        {
            lock (gate) source = MapSource.RemoteFailed;
            log.Warning("[HMSync] opcode map GitHub fetch failed (keeping bundled map): " + ex.Message);
        }
    }

    private static bool Parse(string json, out Dictionary<ushort, string> serverZone, out Dictionary<ushort, string> clientZone, out string version)
    {
        serverZone = new();
        clientZone = new();
        version = "unknown";
        using var doc = JsonDocument.Parse(json);
        foreach (var block in doc.RootElement.EnumerateArray())
        {
            if (!block.TryGetProperty("region", out var reg) || reg.GetString() != "Global") continue;
            if (block.TryGetProperty("version", out var ver)) version = ver.GetString() ?? "unknown";
            if (!block.TryGetProperty("lists", out var lists)) continue;
            LoadList(lists, "ServerZoneIpcType", serverZone);
            LoadList(lists, "ClientZoneIpcType", clientZone);
            return true;
        }
        return false;
    }

    private static void LoadList(JsonElement lists, string listName, Dictionary<ushort, string> into)
    {
        if (!lists.TryGetProperty(listName, out var arr)) return;
        foreach (var e in arr.EnumerateArray())
        {
            if (!e.TryGetProperty("opcode", out var op) || !e.TryGetProperty("name", out var nm)) continue;
            var v = op.GetInt32();
            if (v >= 0 && v <= ushort.MaxValue) into[(ushort)v] = nm.GetString() ?? "";
        }
    }

    // One-line source/staleness summary for the packet-inspector header. "Quietly validated" when we pulled the live
    // community map; a soft "may be stale" hint when we're on the bundled fallback because GitHub was unreachable.
    public string StatusLine()
    {
        MapSource s; string ver, gv;
        lock (gate) { s = source; ver = mapVersion; gv = liveGameVersion; }
        var gvSuffix = string.IsNullOrEmpty(gv) ? "" : "; game " + gv;
        return s switch
        {
            MapSource.Remote => "Opcode names: live map " + ver + " from GitHub" + gvSuffix + ".",
            MapSource.RemoteFailed => "Opcode names: bundled map " + ver + " (couldn't reach GitHub — labels may be stale)" + gvSuffix + ".",
            _ => "Opcode names: bundled map " + ver + " (checking GitHub for a newer one…)" + gvSuffix + ".",
        };
    }

    // Name for an inbound (ServerZone) opcode, or "" if unknown. Inbound is what the packet inspector captures.
    public string InboundName(ushort opcode) { lock (gate) return serverZone.TryGetValue(opcode, out var n) ? n : ""; }
    public string OutboundName(ushort opcode) { lock (gate) return clientZone.TryGetValue(opcode, out var n) ? n : ""; }
}
