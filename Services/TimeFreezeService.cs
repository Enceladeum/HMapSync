using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace HMSync.Services;

// Freezes Eorzea time by HOOKING the game's per-frame UpdateEorzeaTime recompute and no-oping it while frozen, then
// writing the held value into ClientTime.EorzeaTime (0x8) — the field the renderer reads directly. Mechanism ported
// from Brio's TimeService (AGPL — technique reused, credited). This is the CORRECT lever:
//   • Writing EorzeaTimeOverride (0x30) does NOT work — UpdateEorzeaTime recomputes EorzeaTime from the real clock
//     every frame and ignores the override in the render path, clobbering it.
//   • Weatherman patches the render READ (raw byte patch) — works but fragile.
//   • Brio hooks the recompute and disables it — a standard Dalamud hook (same idiom as HMS's other hooks), robust,
//     and the value simply holds once the recompute stops. This is what we use.
public sealed unsafe class TimeFreezeService : IDisposable
{
    // Brio's signature for the UpdateEorzeaTime function.
    private const string UpdateEorzeaTimeSig =
        "48 89 5C 24 ?? 57 48 83 EC ?? 48 8B F9 48 8B DA 48 81 C1 ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C";

    private delegate void UpdateEorzeaTimeDelegate(IntPtr a1, IntPtr a2);
    private readonly Hook<UpdateEorzeaTimeDelegate>? updateHook;
    private readonly IPluginLog log;

    public bool Available => updateHook != null;

    public TimeFreezeService(ISigScanner sig, IGameInteropProvider hooks, IPluginLog log)
    {
        this.log = log;
        try
        {
            var addr = sig.ScanText(UpdateEorzeaTimeSig);
            updateHook = hooks.HookFromAddress<UpdateEorzeaTimeDelegate>(addr, UpdateEorzeaTimeDetour);
            log.Information("[HMSync] TimeFreezeService: UpdateEorzeaTime hook installed.");
        }
        catch (Exception ex)
        {
            updateHook = null;
            log.Error("[HMSync] TimeFreezeService: failed to scan UpdateEorzeaTime — time freeze unavailable: " + ex.Message);
        }
    }

    // While frozen the hook is ENABLED and the detour does nothing (does not call Original) → the per-frame recompute
    // is suppressed → whatever we wrote into EorzeaTime stays.
    private void UpdateEorzeaTimeDetour(IntPtr a1, IntPtr a2)
    {
        // DO NOTHING while frozen — suppress the recompute.
    }

    public bool IsFrozen => updateHook?.IsEnabled ?? false;

    // Freeze at a specific time-of-day (hour/minute), preserving the current Eorzea day.
    public void FreezeAt(int hour, int minute)
    {
        if (updateHook == null)
        {
            log.Warning("[HMSync] [FREEZE] FreezeAt(" + hour + ":" + minute + ") — NO HOOK (sig scan failed at ctor) → cannot freeze");
            return;
        }
        var fw = Framework.Instance();
        if (fw == null) { log.Warning("[HMSync] [FREEZE] Framework null"); return; }

        long cur = fw->ClientTime.EorzeaTime;
        long day = cur - (cur % 86400);                       // midnight of the current Eorzea day
        long tod = ((hour % 24) * 3600L) + ((minute % 60) * 60L);
        long target = day + tod;

        if (!updateHook.IsEnabled) updateHook.Enable();       // stop the recompute FIRST
        fw->ClientTime.EorzeaTime = target;                   // then pin the value the renderer reads
        if (fw->ClientTime.IsEorzeaTimeOverridden)
            fw->ClientTime.EorzeaTimeOverride = target;
    }

    // Freeze at the CURRENT live time (used when the user taps Freeze without dragging — pin "now", not a stale value).
    public void FreezeAtCurrent()
    {
        var (h, m) = GetTimeOfDay();
        FreezeAt(h, m);
    }

    // Release the freeze — the recompute resumes and time flows from the real clock again.
    public void Unfreeze()
    {
        if (updateHook is { IsEnabled: true }) updateHook.Disable();
    }

    // Current Eorzea time-of-day as (hour, minute). When frozen this is our held value (the recompute is off); when
    // not frozen it's the live recomputed clock. Reads EorzeaTime (0x8) — the field the renderer uses.
    public (int hour, int minute) GetTimeOfDay()
    {
        try
        {
            var fw = Framework.Instance();
            if (fw == null) return (0, 0);
            long et = fw->ClientTime.EorzeaTime;
            long tod = ((et % 86400) + 86400) % 86400;
            return ((int)(tod / 3600), (int)((tod % 3600) / 60));
        }
        catch { return (0, 0); }
    }

    public void Dispose()
    {
        try { Unfreeze(); } catch { }
        updateHook?.Dispose();
    }
}
