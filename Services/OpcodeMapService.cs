using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace HMSync.Services;

// Resolves inbound (ServerZone) opcode → packet NAME from the embedded FFXIVOpcodes map (Global region). Opcodes rotate
// per patch, so this is a best-effort label for the packet inspector: if the live client's version differs from the
// embedded map, numbers may not match — the label is a hint, not authority. The NAME is the durable identity we care
// about; the map is swappable (re-embed a newer opcodes.min.json to update).
public sealed class OpcodeMapService
{
    private readonly Dictionary<ushort, string> serverZone = new();   // inbound
    private readonly Dictionary<ushort, string> clientZone = new();   // outbound
    private string mapVersion = "unknown";

    public string MapVersion => mapVersion;

    public OpcodeMapService(IPluginLog log)
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

            using var doc = JsonDocument.Parse(json);
            foreach (var block in doc.RootElement.EnumerateArray())
            {
                if (!block.TryGetProperty("region", out var reg) || reg.GetString() != "Global") continue;
                if (block.TryGetProperty("version", out var ver)) mapVersion = ver.GetString() ?? "unknown";
                if (!block.TryGetProperty("lists", out var lists)) continue;
                LoadList(lists, "ServerZoneIpcType", serverZone);
                LoadList(lists, "ClientZoneIpcType", clientZone);
                break;
            }
            log.Information("[HMSync] opcode map loaded: Global " + mapVersion + " (" + serverZone.Count + " inbound, " + clientZone.Count + " outbound)");
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] opcode map load failed: " + ex.Message);
        }
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

    // Name for an inbound (ServerZone) opcode, or "" if unknown. Inbound is what the packet inspector captures.
    public string InboundName(ushort opcode) => serverZone.TryGetValue(opcode, out var n) ? n : "";
    public string OutboundName(ushort opcode) => clientZone.TryGetValue(opcode, out var n) ? n : "";
}
