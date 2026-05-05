# Lorekeeper - Project Guidelines

## First Read
- Read `VISION.md` immediately before doing any substantive work in this repo.
- Read `FILEMAP.md` at the start of every session to understand the full codebase layout.
- Treat `VISION.md` as the high-level direction for the project.
- Do not assume everything in `VISION.md` is implemented or currently part of the implementation plan. Use the codebase to confirm current behavior.
- Keep this file (`.github/copilot-instructions.md` / `AGENTS.md`) stable and high level. Do not add notes here that are likely to become stale during normal development.

## File Map Maintenance
- After adding, deleting, or renaming any source file, update `FILEMAP.md` to reflect the change.
- When refactoring moves code between files or changes a file's responsibility, update the description in `FILEMAP.md`.
- Keep `FILEMAP.md` entries concise — one to two lines per file maximum.

## Tech Stack
- .NET 10 SDK (pinned via `global.json`, `rollForward: latestFeature`)
- C# with nullable reference types, implicit usings, and `TreatWarningsAsErrors` enabled
- Blazor Web App with **Interactive Server** render mode (SignalR-based circuits, no WebAssembly)
- ASP.NET Core hosting the Razor components and any future minimal APIs

## Architectural Overview
- Single-project server-rendered Blazor app. UI runs on the server over a SignalR circuit; the browser renders HTML fragments pushed from server-side component instances.
- Interactivity is opted into per page/component via `@rendermode InteractiveServer`, not globally. Pages without that directive are statically rendered.
- LLM, vector store, persistence, and background work belong inside this project (or libraries it references) and are consumed directly via DI from components and endpoints.

## Project Structure
- `Lorekeeper.sln` — solution file at repo root
- `global.json` — pins the .NET SDK
- `.editorconfig` — formatting and naming rules for C# and Razor files
- `Lorekeeper/` — the Blazor Web App project
  - `Program.cs` — host setup, DI registration, request pipeline
  - `Components/App.razor`, `Routes.razor`, `_Imports.razor` — root component, router, shared usings
  - `Components/Layout/` — `MainLayout`, `NavMenu`, `ReconnectModal`
  - `Components/Pages/` — routable pages (`Home`, `Error`, `NotFound`)
  - `wwwroot/` — static assets (CSS, favicon, Bootstrap)
  - `appsettings.json` / `appsettings.Development.json` — configuration

## Code Style
- Use `var` when the type is obvious from the right-hand side
- Prefer pattern matching and switch expressions where appropriate
- Follow standard C# naming: PascalCase for public members, _camelCase for private fields

## Build & Run
```bash
dotnet build Lorekeeper.sln
dotnet run --project Lorekeeper
dotnet watch --project Lorekeeper
```

## Conventions
- Use dependency injection for all services
- Configuration via `appsettings.json` and environment variables
- When you need to understand the current wiring, start with `VISION.md`, then `Program.cs`, then the relevant feature area
- Trace each requested change through its full impact area before considering the work complete. Changes to models, contracts, or core concepts should include all affected layers such as persistence, services, queries, prompts, background jobs, and UI.
- Remove superseded code and concepts when replacing them. Do not leave deprecated pages, components, handlers, prompts, queries, or other logic in place just because the new path works; clean out obsolete implementations and reduce unnecessary complexity.
- This is a local development project. When a requested change replaces a concept, remove the superseded implementation outright; do not add or retain compatibility shims, legacy handlers/fallbacks, deprecated tool aliases, or dual paths unless the user explicitly asks for a transition path.
- Never start the app without a plan to also terminate it after verifying the change. Do not leave the app running.