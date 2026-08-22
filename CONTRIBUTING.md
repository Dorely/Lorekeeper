# Contributing to Lorekeeper

Lorekeeper accepts changes through reviewed pull requests. Direct work on
`main` is not part of the repository workflow.

## Start from the current remote

Begin with a clean checkout, then fetch the latest remote state and create a
focused branch from `origin/main`:

```powershell
git fetch --prune origin
git switch -c <type>/<short-topic> origin/main
```

Use a short branch name that describes one coherent change. Do not commit or
push directly to `main`. If the branch is shared or already published, integrate
remote updates without rewriting other contributors' history.

Before pushing, fetch again and ensure the branch includes the latest
`origin/main` and its own remote tracking branch.

## Verify the proposed change

Every commit must pass the complete repository gate on the exact worktree that
will be pushed:

```powershell
dotnet build Lorekeeper.sln
dotnet test Lorekeeper.Tests\Lorekeeper.Tests.csproj
Push-Location Lorekeeper.Press
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test --locked
Pop-Location
```

Also run the impact-specific checks routed by `docs/architecture.md`. Update the
owning architecture chapter and README when the change alters their documented
contracts or user-facing behavior.

## Open and review the pull request

Push the work branch and open a pull request targeting `main`. Complete the pull
request checklist, keep the change focused, and explain any validation that was
not performed.

A pull request may merge only after:

- the repository commit gate succeeds;
- at least one reviewer other than the latest pusher approves it;
- stale approvals are replaced after new commits; and
- every review conversation is resolved.

Do not approve or merge your own pull request. After merge, delete the remote
work branch. A change is integrated only when GitHub reports its pull request as
merged.

## Releases

Release preparation is ordinary repository work: make the version changes on a
dedicated branch, run the full gate, and merge them through a reviewed pull
request.

Publishing requires a fresh non-`main` orchestration branch at the exact fetched
`origin/main` commit. Supply the merged release-preparation pull request number:

```powershell
git fetch --prune origin
git switch -c release/publish-<version> origin/main
.\scripts\publish-release.ps1 -Version <version> -MergedPullRequest <number>
```

The publisher refuses an unmerged or non-current release-preparation pull
request. It also stops when any pull request targeting `main` remains open. A
maintainer may rerun with `-ConfirmOpenPullRequests` only after explicitly
reviewing those open pull requests and confirming the release should proceed.
