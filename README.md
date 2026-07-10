# Lorekeeper

Lorekeeper is a local Blazor Server proof-of-concept for AI-assisted long-form
story planning, drafting, research, ingest, and publishing.

## Requirements

- .NET 10 SDK
- Node.js 22 or later for Electron.NET desktop builds

## Web Development

Run the normal browser-hosted app:

```bash
dotnet run --project Lorekeeper --launch-profile http
```

The HTTP launch profile is pinned to `http://localhost:1455` for the Codex OAuth
callback. Use this explicit profile for browser-driven UI validation; the Electron
profile intentionally remains the default development target.

## Desktop Development

Run the Electron.NET desktop shell:

```bash
dotnet run --project Lorekeeper --launch-profile electron
```

Desktop binding is configured in `Lorekeeper/appsettings.json` under
`Desktop:BindHost` and `Desktop:HttpPort`. Override the port in PowerShell with:

```powershell
$env:Desktop__HttpPort = '1456'
dotnet run --project Lorekeeper --launch-profile electron
```

Codex OAuth uses `Auth:Codex:RedirectUri`, which defaults to
`http://localhost:1455/auth/callback`. Changing the desktop port can break Codex
OAuth unless that redirect URI is also accepted by the OAuth provider.

## Desktop Packaging

Build the Windows desktop package locally:

```bash
dotnet publish Lorekeeper/Lorekeeper.csproj -c Release /p:PublishProfile=win-x64
```

Linux and macOS packages use the matching publish profiles in
`Lorekeeper/Properties/PublishProfiles/`. Electron.NET/electron-builder may
require building on the target OS, except for supported Windows-to-Linux WSL
flows.

## Local Data

SQLite databases, API keys, OAuth tokens, temporary verification databases, and
publish output are local development state and are ignored by git.
