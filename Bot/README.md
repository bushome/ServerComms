# ServerComms.Bot

The Discord side of ServerComms: crosschat, player lists, channel topic and status, account linking,
and (coming) panels and RCON. One bot per game cluster, sharing that cluster's `servercomms` database
with the plugins.

## Build

Open `ServerComms.Bot.sln` in Visual Studio 2026 and build, or publish a single exe:

```
dotnet publish -c Release -r win-x64
```

The exe is in `bin\Release\net10.0\win-x64\publish\`. The server needs the .NET 10 Runtime.

## Install

1. Copy `ServerComms.Bot.exe` to its own folder on the server, outside the game install, e.g.
   `C:\ServerComms.Bot\`. **Never put it in `ArkApi\Plugins\`**: ArkApi tries to load the DLLs it
   finds there, and game updates or plugin redeploys could overwrite the bot's settings. The plugin
   and the bot are separate installs that only share the database.
2. Copy `appsettings.example.json` next to it as `appsettings.json` and fill it in. Until the
   required values are set, the bot refuses to start and lists what to fix.
3. Run the exe to test it (logs go to the window and to `logs\`), then from an administrator prompt:
   - `ServerComms.Bot.exe install`: Windows service, starts with Windows, restarts on failure.
   - `ServerComms.Bot.exe uninstall`: removes the service.

Never run two programs on the same bot token at once: stop the old bot first.

In the Discord developer portal, the bot needs **Message Content Intent** and **Server Members
Intent**. Its role must be above the roles it gives out (Server Settings > Roles).

## Config notes

- **Write Discord IDs in quotes** (`"GuildId": "399784099163668491"`). They are longer than JavaScript
  numbers can hold, so online validators and formatters such as JSONLint silently round unquoted IDs.
  The bot reads both forms; quoted is safe.
- Online validators also reject `//` comments. The example file has them; strip them, or validate
  with a tool that allows comments.
- Settings are read at startup; restart the bot (or the service) after editing.

## Database

The bot creates and updates the tables on startup from `schema\NNN_*.sql` (built into the exe) and
keeps retrying if MariaDB isn't reachable yet. Its own state (chat position, list message IDs) is in
the `bot_state` table, so restarts resume where they stopped.
