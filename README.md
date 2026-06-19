# Stellar.LoadoutSwitcher

StellarResonance plugin. Binds a hotkey to each saved in-game **Loadout** (the game's
"Role Plan" system — class + gear + spec + modules) so a keypress triggers the game's own
loadout switch, and shows the result via the framework notification toasts. Builds against
the published SDK (no framework checkout or game install):

```bash
dotnet build -c Release
```

Requires framework **>= 1.2.0** (adds `ILoadout` + `INotifications`).
