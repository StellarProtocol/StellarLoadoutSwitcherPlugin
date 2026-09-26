# Stellar.LoadoutSwitcher

StellarResonance plugin. Binds a hotkey to each saved in-game **Loadout** (the game's
"Role Plan" system — class + gear + spec + modules) so a keypress triggers the game's own
loadout switch, and shows the result via the framework notification toasts. Builds against
the published SDK (no framework checkout or game install):

```bash
dotnet build -c Release
```

Requires framework **>= 1.2.0** (adds `ILoadout` + `INotifications`).

## Where your Deep-Slumber bindings are stored

A Deep-Slumber binding is **user data** — you captured it with "Bind current" — so from **2.4.0** it
lives in the plugin's own data store: `<game_mini>/stellar/plugindata/
stellar.loadoutswitcher.data/bindings.json` (the framework's `IPluginDataStore`), one compact UTF-8
JSON document. The shared `stellar/plugins/stellar.loadoutswitcher.config.json` keeps **settings**
only — here just the `autoApply` toggle. Writes to the data store are synchronous and atomic
(temp file + rename) and land outside both the config file watcher and the plugin-DLL scan path.
Pressing the hotkey of the loadout you already have equipped re-applies its bound Deep-Slumber setup
(useful when an apply didn't take).

Bindings saved by 2.3.0 or earlier are **migrated automatically**, and the plugin logs one line:

```
[LoadoutSwitcher] migrated 3 Deep-Slumber binding(s) for loadout(s) 1,2,3 from config to plugindata (config keys left in place, frozen, so a rollback still finds them)
```

The migration runs once the game reports your loadout list (the plugin can read a config key but not
enumerate one, so the loadout ids have to come from the game), and each id is consulted exactly once
— recorded in the document — so a binding you later clear is never resurrected from the old config.
The migration only ever **copies**: the `binding.<id>#` config keys are left exactly as they were and
are never written again, so rolling back to 2.3.0 still finds them.
