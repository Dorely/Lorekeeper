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

Build a clean, versioned Windows x64 release from PowerShell:

```powershell
.\scripts\build-windows-release.ps1 -Version 0.2.0
```

Omit `-Version` to use the version in `Lorekeeper.csproj`. The script verifies
the solution build, clears only generated Windows staging/output, creates the
installer and portable executable, and writes `SHA256SUMS.txt`. It needs network
access when npm or Electron dependencies are not already cached.

The underlying packaging command is:

```bash
dotnet publish Lorekeeper/Lorekeeper.csproj -c Release /p:PublishProfile=win-x64
```

The build produces a per-user NSIS installer and a portable executable in
`publish/win-x64/`. The installer is the recommended file to share. It installs
without administrator rights, and recipients do not need .NET or Node.js. Share
`publish/win-x64/Lorekeeper-Setup-<version>-x64.exe` with testers.

For a GitHub Release, upload the Setup executable and the generated
`publish/win-x64/SHA256SUMS.txt`. The `/publish/` directory is intentionally
git-ignored; release binaries should be attached to the GitHub Release rather
than committed to the repository.

Release builds store the SQLite database under
`%LOCALAPPDATA%\Lorekeeper\Data\`, outside both the installed application and
the portable executable's temporary extraction directory. Desktop development
continues to use the repository-local database so existing development data is
not moved.

These local builds are not code-signed. Windows will identify the publisher as
unknown and may show a Microsoft Defender SmartScreen warning. Code signing is
required before distributing beyond a small group of trusted testers.

Linux and macOS packages use the matching publish profiles in
`Lorekeeper/Properties/PublishProfiles/`. Electron.NET/electron-builder may
require building on the target OS, except for supported Windows-to-Linux WSL
flows.

## Local Data

SQLite databases, API keys, OAuth tokens, temporary verification databases, and
publish output are local state and are ignored by git. Installed Windows builds
keep all of that user-specific state in `%LOCALAPPDATA%\Lorekeeper\Data\`.
