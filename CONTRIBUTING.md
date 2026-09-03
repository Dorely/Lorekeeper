# Contributing to Lorekeeper

Lorekeeper accepts changes through reviewed pull requests. Direct work on
`main` is not part of the repository workflow.

## Start from the current remote

Normal development uses one reusable branch under `work/`; it does not create a
branch per feature or topic. Begin with a clean checkout, fetch the latest remote
state, and locate the existing work branch:

```powershell
git fetch --prune origin
git branch --list "work/*"
```

If exactly one work branch exists, switch to and reuse it. Create a generic work
branch such as `work/current` from `origin/main` only when none exists. If more
than one exists, resolve ownership before changing files rather than creating
another branch. Do not commit or push directly to `main`.

After every fetch, ensure `origin/main` is an ancestor of the work branch. A work
branch with no unique commits can fast-forward to `origin/main`; a work branch
with accumulated commits merges `origin/main` without rebasing or rewriting
history. The synchronized work branch may be ahead of `main`, but it must not
have commits on both sides of the comparison.

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

## Accumulate changes and open the pull request

Keep coherent, verified commits on the work branch while the requested work is
still accumulating. Completing an individual change does not require a new pull
request. Push the branch when it needs to be shared or backed up.

When the maintainer decides the accumulated branch is ready to merge, fetch and
incorporate the latest `origin/main`, rerun the full repository gate on the exact
head, inspect the complete branch diff, and open one pull request from the
reusable work branch to `main`. Complete the pull request checklist and explain
any validation that was not performed.

A pull request may merge only after:

- the repository commit gate succeeds;
- at least one reviewer other than the latest pusher approves it;
- stale approvals are replaced after new commits; and
- every review conversation is resolved.

Do not approve or merge your own pull request. Use a merge commit, not squash or
rebase merge, so `main` retains the submitted work-branch ancestry. After GitHub
reports the pull request merged, fetch `origin`, verify that `origin/main`
descends from the submitted head, and fast-forward the reusable local work
branch to `origin/main`. Fast-forward its remote branch as well when one exists,
then reuse the same work branch for the next cycle. If ancestry does not match,
stop rather than resetting or beginning new work on divergent history.

## Releases

When the maintainer decides the accumulated work is ready for release, make the
version changes as the final commit on the reusable work branch, run the full
gate, and include that commit in the branch's reviewed pull request. Do not
create a separate release-preparation branch or pull request.

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
