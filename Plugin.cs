using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.LoadoutSwitcher;

/// <summary>
/// Hotkey-driven loadout switcher. Declares 8 bindable actions (<c>loadout.apply.1</c> …
/// <c>loadout.apply.8</c>, no suggested defaults — the user binds them in Settings →
/// Hotkeys). Pressing the n-th hotkey switches to the n-th saved loadout in
/// <see cref="ILoadout.GetSlots"/> order via the game's own switch
/// (<see cref="ILoadout.ApplyAsync"/>), which runs every server-side validation
/// (combat-lock etc.) — this plugin never bypasses it.
///
/// <para>No overlay window yet (follow-up after the core switch is verified). The actual
/// switch success/failure is toasted by the GAME itself (the switch goes through the
/// game's own <c>AsyncSwitchRolePlan</c> wrapper, which shows the game's success/error
/// toast), so this plugin only toasts its OWN guard messages via
/// <see cref="INotifications"/> (API-not-ready, empty slot, switch-in-flight); switch
/// outcomes additionally surface to the log for diagnostics.</para>
/// </summary>
public sealed partial class Plugin : IStellarPlugin
{
    private const int SlotCount = 8;

    public string Name => "LoadoutSwitcher";

    private readonly IPluginServices _services;
    private readonly IHotkeyAction[] _actions;

    // 0 = idle, 1 = a switch is in flight. Guards against firing a second switch
    // while one is outstanding (the game allows one server-side switch at a time).
    private int _inFlight;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _services.Log.Info("[LoadoutSwitcher] plugin constructed");

        _actions = new IHotkeyAction[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            var n = i + 1;   // 1-based slot number captured per action
            _actions[i] = _services.Hotkeys.DeclareAction(
                new HotkeyAction(
                    Id:               $"loadout.apply.{n}",
                    Description:       $"Apply Loadout {n}",
                    SuggestedDefault:  null),
                callback: () => OnApply(n));
        }
    }

    public void Dispose()
    {
        foreach (var action in _actions)
        {
            try { action.Dispose(); }
            catch { /* disposal must not throw */ }
        }
    }

    // Hotkey n → the n-th loadout in GetSlots() order (slot position, 1-based).
    private void OnApply(int slotNumber)
    {
        if (!_services.Loadout.IsAvailable)
        {
            DiagSkipped(slotNumber, "loadout API unavailable");
            _services.Notifications.Notify("Loadout API not ready", NotificationKind.Warning);
            return;
        }

        IReadOnlyList<LoadoutSlot> slots = _services.Loadout.GetSlots();
        if (slotNumber - 1 >= slots.Count)
        {
            _services.Log.Info($"[LoadoutSwitcher] No loadout in slot {slotNumber}");
            _services.Notifications.Notify($"No loadout in slot {slotNumber}", NotificationKind.Warning);
            return;
        }

        // Reject overlapping switches: only one server-side switch may be in flight.
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            DiagSkipped(slotNumber, "a switch is already in flight");
            _services.Notifications.Notify("Switch already in progress", NotificationKind.Info);
            return;
        }

        var slot = slots[slotNumber - 1];
        DiagApplying(slotNumber, slot);
        _ = ApplyAndReportAsync(slot);
    }

    private async Task ApplyAndReportAsync(LoadoutSlot slot)
    {
        try
        {
            var result = await _services.Loadout.ApplyAsync(slot.Index).ConfigureAwait(false);
            Report(slot, result);
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[LoadoutSwitcher] switch to '{slot.Name}' threw: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    private void Report(LoadoutSlot slot, LoadoutResult result)
    {
        var message = result switch
        {
            LoadoutResult.Success           => $"Switched to {slot.Name}",
            LoadoutResult.InCombat          => "Can't switch loadout in combat",
            LoadoutResult.NoSuchLoadout     => $"Loadout '{slot.Name}' no longer exists",
            LoadoutResult.Rejected          => $"Switch to '{slot.Name}' was rejected",
            LoadoutResult.Timeout           => $"Switch to '{slot.Name}' timed out",
            LoadoutResult.Cancelled         => $"Switch to '{slot.Name}' was cancelled",
            LoadoutResult.GameApiUnavailable => "Loadout switching is not available right now",
            LoadoutResult.PlayerNotInWorld  => "Can't switch loadout — not in world",
            _                               => $"Switch to '{slot.Name}': {result}",
        };
        _services.Log.Info($"[LoadoutSwitcher] {message}");

        // Our success toast NAMES the loadout — the game's own switch toast is generic ("Switched to
        // the new loadout!"). Only the Success case is toasted here; the game already toasts the
        // failure reasons (InCombat / Rejected / …), so we don't double up on those.
        if (result == LoadoutResult.Success)
            _services.Notifications.Notify($"Switched to {slot.Name}", NotificationKind.Success);
    }
}
