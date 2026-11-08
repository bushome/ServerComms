# ServerComms (ASE)

ARK: Survival Evolved build of ServerComms, for AseApi (ArkServerApi 3.56). See the repository README
for what the plugin does; this file covers building, installing and running the ASE plugin.

## Build

**Shared API checkout (optional):** the project builds against `extern\AseApi` by default. To build several plugins against one shared AseApi checkout instead, set the MSBuild property `AseApiDir` to that folder, for example in a `Directory.Build.props` placed above the repo; the submodule then doesn't need to be checked out.

1. `git submodule update --init --recursive` (fetches `extern/AseApi`; `ArkApi.lib` ships in it).
2. Open `ServerComms.sln` in Visual Studio 2026 and build **Release | x64**. vcpkg (bundled with Visual
   Studio) installs libmariadb from `vcpkg.json` on the first build.
3. The ready-to-install plugin folder is `out\ServerComms\`.

## Install

**VC++ runtime:** built with the v145 toolset (Visual Studio 2026) and `_DISABLE_CONSTEXPR_MUTEX_CONSTRUCTOR`, so it also runs on servers with an older Visual C++ runtime. If the server crashes inside `std::mutex` or reports a missing `msvcp140.dll` entry point after installing, install the current Visual C++ Redistributable (x64).

1. Copy `out\ServerComms\` to `ShooterGame\Binaries\Win64\ArkApi\Plugins\ServerComms\` on every map.
2. Create the database and a user for it (`servercomms` by default). ServerComms.Bot creates the
   tables on its first start; until then the plugin keeps retrying and chat stays local.
3. Edit `config.json` (database login, maps, style rules), then restart the map or run
   `ServerComms.Reload`.

The plugin waits until the map has finished loading before it touches the database. If the database
is unreachable, the server runs normally: chat stays on the map, outgoing messages are kept (up to
`Intervals.OutboxCapacity`) and sent once the database answers again.

## Admin commands (RCON and admin console)

| Command | What it does |
| --- | --- |
| `ServerComms.Reload` | Re-reads config.json. A bad file keeps the previous settings and logs why. |
| `ServerComms.Mute <player> <minutes\|perm> <reason>` | Mutes a player (SteamID64, or an online character name) in every chat mode, on every map. |
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
cl /std:c++20 /EHsc /utf-8 /I extern\AseApi\version\Core\Public /I src tests\logic_tests.cpp
logic_tests.exe configs\config.json --shipped
logic_tests.exe configs\config.commented.json --shipped
```

## Files

| Path | Contents |
| --- | --- |
| `src/Main.cpp` | Hooks, timer, database work, commands (game side) |
| `src/ChatLogic.h` | Game-free chat logic, identical in the ASA build |
| `src/Database.h` | MariaDB connection and background worker |
| `src/PermissionsApi.h` | Runtime lookup of the Permissions plugin's GetPlayerGroups |
| `configs/` | `config.json`, `config.commented.json` (same values, explained), `PluginInfo.json` |
