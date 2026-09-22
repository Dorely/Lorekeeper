# Lorekeeper - Agent Operating Guidelines

## Required Context

- At the start of every session, read `VISION.md` and `docs/architecture.md`
  completely before doing substantive work.
- Use the routing table in `docs/architecture.md` after initial repository
  exploration, then read every matching chapter under `docs/architecture/`
  completely before planning or changing that area. If the discovered impact
  expands, read the additional chapter before continuing.
- Treat `VISION.md` as product direction, `docs/architecture.md` as the compact
  architecture and routing authority, and the routed chapters as the current
  technical and product-constraint reference. None of them is proof that a
  feature is already implemented.
- Confirm current behavior in code before planning or changing it.
- Keep this file limited to durable agent behavior. Put product direction in
  `VISION.md`; put technical decisions, boundaries, and changing implementation
  guidance in `docs/architecture.md` and its routed chapters.

## Repository Readiness

- Work on whatever branch is already checked out. Before beginning new work,
  if the current branch is not `main`, merge the latest `main` into it first.
- If existing changes form coherent prior work, finish their verification and
  documentation, then commit them before beginning a new feature. Never mix
  unrelated unfinished work into a new change.
- Inspect every existing diff before committing it. If changes are unfamiliar,
  incomplete, unsafe to commit, or owned by another active effort, stop and ask
  for direction instead of discarding, hiding, or overwriting them.

## Research and Impact Analysis

- Research the codebase before implementation. Use `rg` or an equivalent fast
  search to find every use of the feature, concept, type, route, setting, and
  terminology being changed.
- Trace direct and indirect impact through domain models, EF persistence and
  migrations, repositories, graph/vector/search indexes, services, providers,
  dependency registration, assistant tools and prompts, app-process runtimes,
  visible workspace state, Razor UI, JavaScript, serialization, configuration,
  media endpoints, import/export, publishing, documentation, and desktop/release
  tooling wherever applicable.
- Read callers and consumers, not only the file named in the request. Treat the
  requested location as a starting point rather than the full scope.
- Before replacing a concept, identify all old names, registrations, stored
  forms, and code paths that must disappear. Search for them again after
  implementation.
- For nontrivial work, form an impact plan before editing and keep it current as
  new dependencies are discovered.

## Roslynk Semantic Workflow

- The project-scoped `.codex/config.toml` pins Roslynk and intentionally exposes
  only its read-only tools. The Command Code harness is pinned to the same
  server through the project-scoped `.mcp.json`, with `.commandcode/settings.json`
  permission rules exposing the same 14 read-only tools and denying the mutating
  tools plus `reload_solution`. When available, use Roslynk first for compiled C# and
  Razor semantic questions: diagnostics, symbols, definitions, references,
  callers, implementations, type hierarchies, code-action discovery, and
  dead-code candidates. Continue to use `rg` and host file reads for text search
  and files outside the compiled solution, and use the host `apply_patch` tool
  for edits.
  Roslynk is not required, so fall back to normal repository tools if it cannot
  start without weakening verification.
- Start a Roslynk session by calling `open_solution` with the absolute path to
  `Lorekeeper.sln`. If indexing is incomplete, poll `get_solution_status` before
  relying on other results.
- Roslynk supplements rather than replaces repository impact analysis. Continue
  to use `rg`, inspect callers and consumers, and run all required builds, tests,
  startup checks, native checks, JavaScript checks, and documentation review.
  Treat Roslynk diagnostics as fast edit-loop feedback, not final verification.
- Do not call `reload_solution` proactively. Roslynk watches the workspace; if
  its view appears stale, report the evidence and ask before forcing a reload.
- Treat dead-code and missing-code-action results as candidates, not proof.
  Confirm reflection, dependency-injection, serialization, external-boundary,
  and analyzer behavior in source. Roslynk 1.1.0 does not reliably surface code
  fixes from every third-party analyzer.
- Open only this trusted solution. Roslyn analysis can execute the solution's
  analyzers and source generators in the local Roslynk process, so the read-only
  tool allowlist is not a security sandbox. The loopback daemon may outlive its
  stdio bridge and write `%LOCALAPPDATA%\Roslynk\daemon.log`; never expose it or
  load unrelated sensitive workspaces. Daemon-wide status can include every
  solution that process has loaded.

## Implementation Standards

- Deliver the smallest coherent change that fully completes the requested
  behavior across every affected layer.
- Reuse or extend existing code, components, contracts, and patterns wherever
  possible. Search for an existing implementation before creating another one.
- Treat future maintainability as a first-class requirement. Prefer clear
  ownership, cohesive feature areas, explicit contracts, consistent naming, and
  straightforward control flow over locally convenient shortcuts.
- Add an abstraction only when it creates a clear boundary or removes meaningful
  duplication. Do not create parallel helpers or generic dumping grounds.
- Use dependency injection for services and repositories. Keep Razor components
  focused on interaction and presentation state; put persistence, provider
  resolution, graph/index maintenance, assistant behavior, image workflows,
  publishing, and background work in their owning services.
- Keep provider-specific transport details behind provider-neutral contracts.
  Put configuration in `appsettings.json` and environment variables.
- Keep credentials inside the existing provider and OAuth-token persistence
  boundaries. Do not copy secrets onto unrelated feature entities, expose them
  in tool payloads, or log API keys, authorization codes, or tokens.
- **Hard rule: never leave the runtime in an obsolete state.** When a feature,
  concept, name, model, configuration, or code path becomes unused or is
  superseded, delete it completely in the same change.
- Do not retain dead branches, commented-out implementations, stale
  registrations, duplicate paths, compatibility shims, legacy aliases, or
  outdated documentation. If persisted data or an external boundary requires a
  transition, complete the migration and remove the old runtime path as part of
  the feature; otherwise stop and obtain an explicit migration plan.
- Preserve applied EF Core migrations as immutable schema history. Represent
  schema changes with forward migrations; historical migration files are not
  runtime compatibility paths and must not be deleted merely because their
  original model was later superseded.
- Preserve unrelated user changes. If they prevent a clean starting state, stop
  and resolve ownership before implementation. Never use a destructive reset or
  checkout to simplify the task.

## Application-Owned Dialogs

- Product confirmations, alerts, prompts, pickers, and modal workflows must be
  rendered by Lorekeeper's Razor/HTML/CSS components. Never invoke browser
  `alert`, `confirm`, or `prompt`, Electron dialog APIs, operating-system message
  boxes, or another native product dialog as application UI.
- Use the shared application confirmation component for destructive decisions
  and keep validation and operation failures inside the owning Lorekeeper
  surface. Do not let browser or Electron chrome replace application context.
- Browser-mediated local-file selection is permitted only at explicit
  import/upload boundaries where the web security model requires it. Opening an
  external browser is permitted only for an intentional, documented handoff such
  as OAuth, vendor documentation, or application updates.
- Printing uses the shared application-owned print preview. Its explicit Print
  action may open the browser/system printer dialog as a narrow device handoff;
  this exception does not permit native dialogs for other product workflows.

## Code Style

- Follow `.editorconfig` as the code-style and naming authority.
- Use `var` when the type is obvious from the right-hand side.
- Prefer pattern matching and switch expressions where they make the intent
  clearer.
- Use PascalCase for public members and `_camelCase` for private instance fields,
  consistent with the configured naming rules.
- Keep Electron-specific behavior in the desktop host path and provider-specific
  behavior in the owning adapter. Shared narrative-workspace behavior belongs
  behind the service contracts described in `docs/architecture.md`.

## Documentation Maintenance

- Update the owning routed architecture chapter in the same change whenever
  work alters its contracts, component responsibilities, data flow, persistence,
  provider behavior, platform support, security posture, validation, primary
  entry points, or key file families.
- Update `docs/architecture.md` only when global invariants, chapter boundaries,
  or routing rules change. Keep the index compact and keep file descriptions in
  exactly one owning chapter rather than duplicating them across consumers.
- Update `README.md` when user-facing capabilities, requirements, setup, run,
  packaging, release, update, or local-data behavior changes.
- Update `VISION.md` only when the product direction or scope has intentionally
  changed.
- Remove stale comments, examples, documentation, settings, and instructions as
  part of the feature that makes them obsolete.

## Verification

- **Completion rule (not a scripted gate): do not finish a change until
  `dotnet build Lorekeeper.sln` succeeds and the remaining tests pass** —
  `dotnet test Lorekeeper.Tests\Lorekeeper.Tests.csproj`, and `cargo test` in
  `Lorekeeper.Press` when Press sources are affected. Run these on the final
  state of the change; earlier results do not count. There is no automated
  commit gate.
- Verify changes in proportion to their impact using the relevant static
  checks and release checks documented in `docs/architecture.md` and its
  routed chapters.
- Automated tests in `Lorekeeper.Press` may exist only when they map to a
  requirement in the Press conformance evidence matrix. This includes the
  protocol, containment, atomicity, cancellation, determinism, typography,
  layout, raw-PDF, and adversarial evidence needed to trust the PDF result.
  This is the Press TDD workflow and stays.
- Automated tests in `Lorekeeper.Tests` are limited to the data-safety core:
  startup database migration, versioned project import/export, archive, and
  history preservation and fail-closed behavior, and recovery.
- **Automated tests outside those two scopes are forbidden.** Do not create
  new test projects, test suites, or one-off test harnesses anywhere else. A
  request to add tests does not broaden this boundary unless the user
  explicitly changes it.
- **Manual and UI validation requires the user's explicit authorization
  before it is performed.** This includes startup smoke checks, Electron
  checks, browser/UI checks, screenshots, and interactive provider or Word
  exercises. Ask first; never run them as routine verification. When
  authorized, terminate every host, Electron shell, browser helper, and
  background process afterward, and do not start a competing host on a port a
  user-owned debug instance already occupies.
- Electron is the primary/default debug target. The explicit `http` launch
  profile at `http://localhost:1455` is the browser validation surface; do not
  reorder the launch profiles to make browser hosting the default.
- Do not claim OAuth, provider calls, embeddings, web search, image generation,
  publishing, release packaging, automatic updates, or platform-specific
  Electron behavior works unless the relevant integration and target platform
  have actually been exercised. Clearly report any validation that remains
  unperformed.
- Before completion, search for obsolete names and paths, inspect the complete
  diff, and confirm that documentation matches the resulting code.
- The release driver runs its build-and-test preflight before the version
  commit and again before publication. Release packaging checks are additional
  evidence. The exact commit being released must be the verified commit.
- For a user-authorized direct stable release, prefer `scripts/release.ps1` on
  clean `main`. It owns version selection, the version-only commit, both
  preflights, pushing, and publication. Use `-CheckOnly` when asked to preview
  or validate the driver without releasing. Do not run its publishing path
  merely to test changes to release tooling. It must never commit unrelated
  unfinished work.
- Invoke the publisher only from a clean tree whose `HEAD` equals fetched
  `origin/main`, with the explicit `-AllowDirectMainPush` authorization. The
  script enforces these preconditions itself.

## Completion and Commits

- A feature is complete only when its full impact area is implemented, obsolete
  runtime code is removed, documentation is current, and relevant verification
  succeeds.
- Once a feature is complete and the build and remaining tests pass plus all
  impact-specific verification succeeds, inspect the final diff and status,
  stage only that feature's files, and create a focused commit with a descriptive
  message.
- Commit every completed feature before beginning another one. Do not combine
  unrelated work in a single commit.
- After committing, verify that the working tree is clean. Do not amend, squash,
  force-push, or otherwise rewrite history unless explicitly requested.
- If a required commit cannot be created, report the blocker and do not describe
  the feature as completed.
