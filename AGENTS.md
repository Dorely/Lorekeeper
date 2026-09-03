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

- Before beginning new work, inspect the current branch, working tree, index,
  configured remotes, and upstream status.
- Never edit, commit, or push directly on `main`. Normal repository work uses
  exactly one reusable local branch whose name starts with `work/`. Do not create
  per-feature, per-task, or per-topic branches, and do not create a second
  `work/` branch while one already exists.
- At the start of a session, fetch `origin` with pruning and locate the existing
  `work/` branch before changing files. If exactly one exists, continue on it.
  If multiple `work/` branches exist, or uncommitted work belongs to another
  branch, stop and ask for direction rather than choosing, moving, or hiding
  work. Create a generic reusable `work/` branch from `origin/main` only when no
  work branch exists and the checkout is clean and synchronized.
- Before new work, require the reusable work branch to contain the current
  `origin/main`. Fast-forward it when it has no unique commits; otherwise merge
  `origin/main` into it without rebasing or rewriting history. After
  synchronization, `origin/main` must be an ancestor of the work branch so its
  history is equal to or strictly ahead of `main`, never locally diverged.
- If existing changes form coherent prior work, finish their verification and
  documentation, then commit them before beginning a new feature. Never mix
  unrelated unfinished work into a new change.
- Inspect every existing diff before committing it. If changes are unfamiliar,
  incomplete, unsafe to commit, or owned by another active effort, stop and ask
  for direction instead of discarding, hiding, or overwriting them.
- When an upstream exists, fetch it with pruning before work, before final
  verification, and immediately before pushing. Ensure the work branch includes
  the current `origin/main` and has no unintegrated commits from its own remote
  tracking branch. Integrate remote changes without rewriting published history;
  stop for direction if histories have diverged or ownership is unclear.
- When no remote or upstream exists, require a clean local `HEAD` and report that
  remote synchronization could not be checked.

## Branch and Pull Request Workflow

- Every repository change, including release preparation, documentation, and
  workflow configuration, must enter `main` through a pull request from a
  non-`main` branch. Direct pushes to `main` are prohibited even for
  administrators and urgent fixes.
- Accumulate coherent, verified commits on the same reusable work branch until
  the user decides that the accumulated changes are ready to merge. Do not open
  or update a pull request without explicit request from the user.
- When the user decides to merge the accumulated work, fetch `origin`, merge the
  latest `origin/main` into the work branch if needed, inspect the complete
  branch diff, rerun all required verification on the exact proposed head, push
  only the reusable work branch, and open or update one pull request targeting
  `main`.
- Merge the pull request with a merge commit. Do not squash-merge or rebase-merge
  it: those methods replace the submitted commit ancestry and make a reused work
  branch diverge from `main`. Do not approve or merge your own pull request;
  wait for the required status checks, an independent approval, and resolution
  of every review conversation.
- After GitHub reports the pull request merged, fetch `origin` and verify that
  `origin/main` descends from the exact submitted work-branch head. Fast-forward
  the local reusable work branch to `origin/main`, then fast-forward its remote
  branch when one exists. Do not delete or replace the local work branch; reuse
  it for the next accumulation cycle. If the merged `origin/main` does not
  descend from the submitted head, stop for direction instead of resetting,
  rebasing, or beginning new work on divergent history.
- A fresh release-orchestration branch is the only exception to the single
  reusable work-branch rule. Create it only after the release-preparation pull
  request has merged, as described below.

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

- Beyond the mandatory repository-wide commit gate below, verify changes in
  proportion to their impact using the relevant static checks, runtime checks,
  and release checks documented in `docs/architecture.md` and its routed
  chapters.
- Automated tests in `Lorekeeper.Press` may exist only when they map to a
  requirement in the Press conformance evidence matrix. This includes the
  protocol, containment, atomicity, cancellation, determinism, typography,
  layout, raw-PDF, and adversarial evidence needed to trust the PDF result.
- Automated tests in `Lorekeeper.Tests` may exist only to prove data
  preservation and fail-closed behavior across application-startup database
  migrations or versioned project import/export transformations.
- Do not add automated UI, assistant, editor, provider, ordinary service,
  packaging, authentication, or runtime-behavior tests. Validate those areas
  through builds, static inspection, and user-authorized manual or browser
  checks. A user request to add tests does not broaden this repository boundary
  unless the user explicitly changes the two approved test purposes.
- Every commit, regardless of its apparent scope, must pass the full repository
  commit gate on the exact final worktree that will be committed. Route .NET
  outputs through the ignored commit-gate artifacts directory so verification
  never requires stopping a developer-owned debug instance that has the default
  `bin/` output locked:

  ```powershell
  $env:ArtifactsPath = Join-Path (Get-Location) ".artifacts\commit-gate"
  dotnet build Lorekeeper.sln
  dotnet test Lorekeeper.Tests\Lorekeeper.Tests.csproj
  Remove-Item Env:ArtifactsPath
  Push-Location Lorekeeper.Press
  cargo fmt --check
  cargo clippy --all-targets -- -D warnings
  cargo test --locked
  Pop-Location
  ```

  Earlier results from another commit or from before the final edit do not
  satisfy this gate. A failure blocks the commit even when the failing test
  appears unrelated, pre-existing, intermittent, or outside the changed area.
  Diagnose the failure; then either correct it and rerun the entire gate, or stop
  and report the blocker. Never commit, push, tag, or publish while any command
  in this gate is failing.
- Verify normal source changes with `dotnet build Lorekeeper.sln`, using the
  isolated `ArtifactsPath` above when a debug instance is running. After a
  successful build, start the browser-hosted app with
  `dotnet run --project Lorekeeper --launch-profile http`, confirm the local host
  starts without startup exceptions, and terminate it. When a user-owned debug
  instance already provides the requested validation surface, use that instance
  without terminating it and do not start a competing host on the same port.
- Electron is the primary/default debug target. For browser-driven validation,
  use the explicit `http` launch profile at `http://localhost:1455`; do not
  reorder the launch profiles to make browser hosting the default.
- Never start a host, Electron shell, browser automation, or background helper
  without a plan to terminate it after validation.
- Do not run Playwright, screenshots, browser UI checks, Electron window checks,
  or manual UI validation unless the user explicitly requests them.
- When Electron startup itself must be smoke-checked, start
  `dotnet run --project Lorekeeper --launch-profile electron`, confirm startup,
  and terminate the app. Never leave the app running.
- Do not claim OAuth, provider calls, embeddings, web search, image generation,
  publishing, release packaging, automatic updates, or platform-specific
  Electron behavior works unless the relevant integration and target platform
  have actually been exercised. Clearly report any validation that remains
  unperformed.
- Before completion, search for obsolete names and paths, inspect the complete
  diff, and confirm that documentation matches the resulting code.
- Immediately before a release version-preparation commit and again before
  invoking a release publisher, run the full repository commit gate. Release
  packaging checks are additional evidence; they never replace the repository
  gate. The exact commit being released must be the verified commit.
- When the user decides the accumulated work is ready for release, add the
  release/version changes as the final commit on the reusable work branch and
  include them in that branch's reviewed pull request. Do not create a separate
  release-preparation branch or pull request. Before publishing, verify that the
  resulting release-preparation pull request is `MERGED`, targets `main`, and
  produced the current `origin/main` commit. If it is not fully merged, a
  release is impossible.
- Invoke the publisher only from a fresh, clean, non-`main` release-orchestration
  branch created at the fetched `origin/main`. If any pull request targeting
  `main` remains open, stop and obtain explicit user confirmation before using
  the publisher's `-ConfirmOpenPullRequests` override. That override never makes
  an unmerged release-preparation pull request releasable.

## Completion and Commits

- A feature is complete only when its full impact area is implemented, obsolete
  runtime code is removed, documentation is current, and relevant verification
  succeeds.
- Once a feature is complete and the full repository commit gate plus all
  impact-specific verification succeeds, inspect the final diff and status,
  stage only that feature's files, and create a focused commit with a descriptive
  message.
- Commit every completed feature before beginning another one. Do not combine
  unrelated work in a single commit. Keep subsequent coherent commits on the
  same work branch until the user decides the accumulated branch is ready to
  merge; do not create a pull request after every commit or feature.
- After committing, verify that the working tree is clean. Push the branch when
  requested or needed for collaboration or backup, but open or update its pull
  request only when the user decides to merge the accumulated work. Do not amend,
  squash, force-push, or otherwise rewrite history unless explicitly requested.
  Repository integration is complete only after the pull request is reviewed,
  all required checks pass, and GitHub reports it merged.
- If a required commit cannot be created, report the blocker and do not describe
  the feature as completed.
