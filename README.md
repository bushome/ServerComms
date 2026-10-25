```
 _____
/  ___|
\ `--.   ___  _ __ __   __  ___  _ __
 `--. \ / _ \| '__|\ \ / / / _ \| '__|
/\__/ /|  __/| |    \ V / |  __/| |
\____/  \___||_|     \_/   \___||_|
 _____                                       .------------------------.
/  __ \                                      | >_ [Island] Rex: hi!_  |     ((( o )))
| /  \/  ___   _ __ ___   _ __ ___   ___     '-----.  .---------------'         |
| |     / _ \ | '_ ` _ \ | '_ ` _ \ / __|          | /                         /|\
| \__/\| (_) || | | | | || | | | | |\__ \          |/  [Discord] Sue: o/      / | \
 \____/ \___/ |_| |_| |_||_| |_| |_||___/                                    /  |  \
```

Cross-server chat for ARK clusters. Global chat links every map in a cluster and relays to and
from Discord through one bot per cluster. It also covers chat styling by group, tribe or player,
moderation (mutes, anti-spam, word filter), Discord account linking, live player and admin lists,
button panels, and an RCON Commander for admins. Data is stored in MariaDB.

**Status: complete.** Both versions and the bot are tested and running in production. This software will receive only bug fixes for future game state version updates.
No new feature sets will be added beyond what is presented here.

| Folder | Part | Runs on | Player key |
|---|---|---|---|
| [`ASA`](ASA/README.md) | ARK: Survival Ascended plugin, [AsaApi](https://github.com/ArkServerApi/AsaApi) at `ASA/extern/AsaApi` | each game server | EOS ID |
| [`ASE`](ASE/README.md) | ARK: Survival Evolved plugin, [ArkServerApi (AseApi)](https://github.com/ArkServerApi/AseApi) at `ASE/extern/AseApi` | each game server | SteamID64 |
| [`Bot`](Bot/README.md) | Discord bot (.NET 10, single exe) | one per cluster, outside the server folders | — |

`ASA` and `ASE` are self-contained Visual Studio solutions. `Bot` is a .NET solution. Clone with submodules:

```
git clone --recurse-submodules https://github.com/bushome/ServerComms.git
```

or, in an existing clone:

```
git submodule update --init --recursive
```

Each cluster uses its own `servercomms` MariaDB database, its own Discord channels and its own bot
token. The plugin and the bot are separate installs that talk only through that database. Never
put the bot in `ArkApi\Plugins\`. The plugin creates its tables on first start, and the bot runs
its own schema migrations. `src/ChatLogic.h` is the same in both plugin folders and is covered by
`tests/logic_tests.cpp`. `src/Main.cpp` and the project files differ by API.

Releases ship three zips: `ServerComms-ASA.zip`, `ServerComms-ASE.zip` and `ServerComms.Bot.zip`
(the self-contained bot, no .NET install needed). Install, commands, configuration and build steps
are in each folder's README.
