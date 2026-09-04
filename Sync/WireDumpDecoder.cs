using System.Text.Json;
using MessagePack;
using HMSync.Wire;

namespace HMSync.Sync;

// S331 (Stage 4): the /hms wiredump payload decoder. CLIENT-ONLY (not in the shared wire file - it needs
// System.Text.Json for named-field display, and the relay never decodes lane payloads).
//
// The fix vs the first draft (flagged by the relay thread): keep the concrete type in each switch branch and
// serialize THAT with System.Text.Json (which uses the C# property names) - NOT re-serialize a boxed `object`
// through MessagePack (which can't serialize a bare object and would always throw → "(decode error)"). And
// System.Text.Json gives NAMED fields ({"X":3.5,"Y":4.2,...}) instead of msgpack's positional array ([3.5,4.2,...]),
// so the output is actually readable without the spec open - which is the whole point of §8.
internal static class WireDumpDecoder
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public static string DecodePayload(byte kind, byte[] payload)
    {
        try
        {
            return kind switch
            {
                WireKind.HotUpdate    => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<HotPayload>(payload, WireFormat.Options), JsonOpts),
                WireKind.WarmUpdate   => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<WarmPayload>(payload, WireFormat.Options), JsonOpts),
                WireKind.ColdUpdate   => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<ColdPayload>(payload, WireFormat.Options), JsonOpts),
                WireKind.HostUpdate   => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<HostPayload>(payload, WireFormat.Options), JsonOpts),
                WireKind.DisguiseUpdate => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<DisguiseUpdatePayload>(payload, WireFormat.Options), JsonOpts),
                WireKind.ActionPulse  => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<ActionPulsePayload>(payload, WireFormat.Options), JsonOpts),
                WireKind.PuppetMove   => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<PuppetMovePayload>(payload, WireFormat.Options), JsonOpts),
                WireKind.OwnBodyHidden => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<OwnBodyHiddenPayload>(payload, WireFormat.Options), JsonOpts),
                WireKind.LobbyNameplate => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<LobbyNameplatePayload>(payload, WireFormat.Options), JsonOpts),
                WireKind.FreezeUpdate => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<FreezeUpdatePayload>(payload, WireFormat.Options), JsonOpts),
                WireKind.LightsOut    => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<LightsOutPayload>(payload, WireFormat.Options), JsonOpts),
                WireKind.JoinRoom     => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<JoinPayload>(payload, WireFormat.Options), JsonOpts),
                WireKind.RoomJoined   => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<RoomJoinedPayload>(payload, WireFormat.Options), JsonOpts),
                WireKind.PeerJoined   => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<PeerJoinedPayload>(payload, WireFormat.Options), JsonOpts),
                WireKind.PeerLeft     => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<PeerLeftPayload>(payload, WireFormat.Options), JsonOpts),
                WireKind.HostTransfer => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<HostTransferPayload>(payload, WireFormat.Options), JsonOpts),
                WireKind.Error        => JsonSerializer.Serialize(MessagePackSerializer.Deserialize<ErrorPayload>(payload, WireFormat.Options), JsonOpts),
                // v0.7.464: soft-throttle notice - empty payload by contract, so there is nothing to decode.
                WireKind.RateLimited  => "(soft throttle notice, " + payload.Length + " payload bytes)",
                _ => "(" + payload.Length + " payload bytes, no schema for kind 0x" + kind.ToString("X2") + ")",
            };
        }
        catch (System.Exception ex)
        {
            return "(decode error: " + ex.Message + ")";
        }
    }
}
