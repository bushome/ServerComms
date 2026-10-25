# ServerComms (ASA)

ARK: Survival Ascended build of ServerComms, for AsaApi 2.03. See the repository README
for what the plugin does; this file covers building, installing and running the ASA plugin.

## Build

1. `git submodule update --init --recursive` (fetches `extern/AsaApi`), then build `extern\AsaApi\AsaApi.sln`
   (Release | x64) for `extern\AsaApi\out_lib\AsaApi.lib`, or copy it from the AsaApi release zip's `Lib` folder.
2. Open `ServerComms.sln` in Visual Studio 2026 and build **Release | x64**. vcpkg (bundled with Visual
   Studio) installs fmt and libmariadb from `vcpkg.json` on the first build.
3. The ready-to-install plugin folder is `out\ServerComms\`.

## Install

1. Copy `out\ServerComms\` to `ShooterGame\Binaries\Win64\ArkApi\Plugins\ServerComms\` on every map.
2. Create the database and a user for it (`servercomms` by default). ServerComms.Bot creates the
   tables on its first start; until then the plugin keeps retrying and chat stays local.
3. Edit `config.json` (database login, `ServerKey`, maps, style rules), then restart the map or run
   `ServerComms.Reload`.

The plugin waits until the map has finished loading before it touches the database. If the database
is unreachable, the server runs normally: chat stays on the map, outgoing messages are kept (up to
`Intervals.OutboxCapacity`) and sent once the database answers again.

## Admin commands (RCON and admin console)

| Command | What it does |
| --- | --- |
| `ServerComms.Reload` | Re-reads config.json. A bad file keeps the previous settings and logs why. |
| `ServerComms.Mute <player> <minutes\|perm> <reason>` | Mutes a player (EOS ID, or an online character name) in every chat mode, on every map. |
| `ServerComms.Unmute <player>` | Removes a mute. |
| `ServerComms.TestIcon <icon path> <player>` | Sends one chat line with that icon to that player only, to check an icon before adding it to `SafeIcons`. |

## Player commands

| Command | What it does |
| --- | --- |
| `/linkDiscord <code>` | Links the player's Discord account (code from the Link button in Discord). |
| `/hidetag`, `/showtag` | Hides or shows a hideable tag (e.g. Patron). |
| `/ignore`, `/ignore #` | Lists recent speakers, then ignores the chosen one (global chat, every map). |
| `/unignore`, `/unignore #` | Lists ignored players, then removes the chosen one. |

Command names can be changed under `Commands` in config.json.

## Tests

`tests/logic_tests.cpp` tests `src/ChatLogic.h` (styles, formatting, filter, anti-spam, mutes) with no
game or Windows needed:

```
cl /std:c++20 /EHsc /utf-8 /I extern\AsaApi\AsaApi\Core\Public /I src tests\logic_tests.cpp
logic_tests.exe configs\config.json --shipped
logic_tests.exe configs\config.commented.json --shipped
```

## Files

| Path | Contents |
| --- | --- |
| `src/Main.cpp` | Hooks, timer, database work, commands (game side) |
| `src/ChatLogic.h` | Game-free chat logic, identical in the ASE build |
| `src/Database.h` | MariaDB connection and background worker |
| `src/PermissionsApi.h` | Runtime lookup of the Permissions plugin's GetPlayerGroups |
| `configs/` | `config.json`, `config.commented.json` (same values, explained), `PluginInfo.json` |

## Platforms

Chat carries a platform number per sender. `Platforms` in config.json names them (`"1": "Steam"` to
start); the first time an unlisted number is seen, ArkApi.log says so, so it can be added.
