using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HMSync.Services;

// v0.7.339: right-click-to-dismount on the mount-status HUD icon (_StatusCustom2 — the little chocobo badge shown while
// mounted). Behind the packet firewall the game's native right-click on that icon is a no-op: it fires a dismount
// ACTION to the server, which the filter drops (server never acks → nothing happens). So a synthetic-session user
// right-clicking the icon sees no effect. We catch that right-click and route it to HMS's local dismount instead.
//
// APPROACH (v0.7.339.6): listen to the addon's ReceiveEvent via IAddonLifecycle.PreReceiveEvent — NOT a node-event
// handler. The earlier IAddonEventManager.AddEvent(node 2, MouseUp) approach attached fine but never fired: node 2 is
// the component CONTAINER (flags 0x2033 = EmitsEvents but NO HasCollision/RespondToMouse — the collision node lives
// inside it), and Dalamud's node-event delivery doesn't hand us the raw right-click. PreReceiveEvent fires reliably
// for the addon's own event handling (all buttons, incl. right-click) and gives us the event type + button, so we can
// detect the right-click the native handler is already processing and do the real dismount alongside it.
public sealed unsafe class MountHudDismountService : IDisposable
{
    private const string AddonName = "_StatusCustom2";

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Action onDismountClicked;

    private bool active;

    public MountHudDismountService(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IPluginLog log,
        Action onDismountClicked)
    {
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.log = log;
        this.onDismountClicked = onDismountClicked;
    }

    public void Enable()
    {
        if (active) return;
        active = true;
        addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, AddonName, OnReceiveEvent);
    }

    public void Disable()
    {
        if (!active) return;
        active = false;
        addonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, AddonName, OnReceiveEvent);
    }

    private void OnReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs e) return;

        // The mount icon signals a click to its handler as AtkEventType.IconTextClick (61) — NOT a raw mouse event. By
        // this stage the raw button is gone (AtkEventData is the icon-click's own union, so MouseData.ButtonId reads 0
        // even on a right-click — which is why the earlier button-filter never matched). IconTextClick IS the gesture
        // that triggers the native dismount, so match on it directly; dismount is the icon's only meaningful action.
        // Cast the raw value 61 rather than name the generated AddonEventType member (avoids a member-name dependency).
        if ((int)e.AtkEventType != 61) return;   // 61 = AtkEventType.IconTextClick

        // Only act during a synthetic session (outside a session the native right-click works normally — leave it).
        onDismountClicked();
    }

    // v0.7.339 probe (/hms mounthud): confirm the addon is up + dump node ids/flags. Kept for diagnosis.
    public void DebugProbe()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(AddonName, 1);
        if (addon == null)
        {
            log.Information("[HMSync] [MOUNTHUD-DIAG] PROBE: addon '" + AddonName + "' NOT loaded (are you mounted?).");
            return;
        }
        log.Information("[HMSync] [MOUNTHUD-DIAG] PROBE: addon '" + AddonName + "' found, listener active=" + active +
            " visible=" + addon->IsVisible);
    }

    public void Dispose() => Disable();
}

