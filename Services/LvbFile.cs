using System;
using Lumina.Data;

namespace HMSync.Services;

/// <summary>
/// Reads a zone's <c>.lvb</c> (level) file weather table — the FULL per-zone weather set, which includes cinematic
/// weathers (e.g. CutScene) that never appear in the WeatherRate sheet. Parse ported from Weatherman, which credits
/// TitleEdit (https://github.com/lmcintyre/TitleEditPlugin). Loaded via IDataManager.GetFile&lt;LvbFile&gt;.
/// </summary>
public class LvbFile : FileResource
{
    public ushort[] WeatherIds = Array.Empty<ushort>();

    public override void LoadFile()
    {
        WeatherIds = new ushort[32];

        var pos = 0xC;
        if (Data[pos] != 'S' || Data[pos + 1] != 'C' || Data[pos + 2] != 'N' || Data[pos + 3] != '1')
            pos += 0x14;
        var sceneChunkStart = pos;
        pos += 0x10;
        var settingsStart = sceneChunkStart + 8 + BitConverter.ToInt32(Data, pos);
        pos = settingsStart + 0x40;
        var weatherTableStart = settingsStart + BitConverter.ToInt32(Data, pos);
        pos = weatherTableStart;
        for (var i = 0; i < 32; i++)
            WeatherIds[i] = BitConverter.ToUInt16(Data, pos + i * 2);
    }
}
