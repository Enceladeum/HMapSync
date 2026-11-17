namespace HMSync.Services;

using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
// HOutfitsIpc (b292) - reads the local player's HOutfits weapon override so HMS can show it to session peers.
//
// HOutfits can draw another class's weapon onto your own character with the game's weapon loader (draw only: the
// stored weapon and the server never change). Neither Glamourer nor the Mare-family tools carry that, so HMS does:
// the override rides the Cold lane (TransformData.WeaponOverrideMain/Off) and each peer draws it on our body.
//
// CONTRACT (HOutfits provides; namespace "HOutfits.*", same detection pattern as Moniker/HDM):
//   HOutfits.WeaponOverride.ApiVersion : Func<(uint Major, uint Minor)>   major 1
//   HOutfits.WeaponOverride.Get        : Func<(ulong Main, ulong Off)>    WeaponModelId.Value of what is drawn
//       Main == 0 and Off == 0 : no override (the class weapon is shown)
//       Main != 0              : main hand overridden; Off is always drawn with it (0 = empty off hand)
//       Main == 0, Off != 0    : only the off hand is overridden
// HMS polls Get at its 10 Hz capture tick; no event is needed. Absence is soft: no HOutfits, an older HOutfits, or a
// public build without the override all read as "no override", and the probe retries every few seconds.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
public sealed class HOutfitsIpc
{
    private const uint RequiredMajor = 1;
    private const long ProbeIntervalMs = 5000;

    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<(uint, uint)> apiVersion;
    private readonly ICallGateSubscriber<(ulong, ulong)> get;
    private bool available;
    private string lastProbeError = "";   // b294: last probe failure kind, so it is logged once
    private long nextProbe;

    public HOutfitsIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;
        apiVersion = pi.GetIpcSubscriber<(uint, uint)>("HOutfits.WeaponOverride.ApiVersion");
        get = pi.GetIpcSubscriber<(ulong, ulong)>("HOutfits.WeaponOverride.Get");
    }

    /// <summary>b294: whether the IPC answered the last probe (for the wpnsync diagnostic).</summary>
    public bool Available => available;

    /// <summary>The local weapon override as (main, off) WeaponModelId values; (0, 0) when none or HOutfits is absent.</summary>
    public (ulong Main, ulong Off) GetWeaponOverride()
    {
        if (!Probe()) return (0, 0);
        try { return get.InvokeFunc(); }
        catch (Exception ex)
        {
            available = false; nextProbe = Environment.TickCount64 + ProbeIntervalMs;
            log.Information("[HMSync] HOutfits weapon-override IPC stopped answering (" + ex.GetType().Name + "); retrying every 5 s.");
            return (0, 0);
        }
    }

    private bool Probe()
    {
        if (available) return true;
        long now = Environment.TickCount64;
        if (now < nextProbe) return false;
        nextProbe = now + ProbeIntervalMs;
        try
        {
            var (major, minor) = apiVersion.InvokeFunc();
            available = major == RequiredMajor;
            if (available) log.Information($"[HMSync] HOutfits weapon-override IPC {major}.{minor} found: overrides sync to session peers.");
        }
        catch (Exception ex)
        {
            available = false;
            // b294: say once why (per error kind) instead of failing silently. IpcNotReadyError = HOutfits not loaded, or its
            // IPC was unregistered (unloading a second HOutfits copy removes the shared registration until HOutfits reloads).
            if (lastProbeError != ex.GetType().Name)
            {
                lastProbeError = ex.GetType().Name;
                log.Information("[HMSync] HOutfits weapon-override IPC not available (" + lastProbeError + "); HMS keeps retrying every 5 s.");
            }
        }
        if (available) lastProbeError = "";
        return available;
    }
}
