# GitHub repository settings

This is the maintainer checklist for making `main` a reviewed, pull-request-only
branch. Repository instructions assume these controls are active. The repository
currently has no pull-request validation workflow, so the full commit gate is
run locally and recorded in the pull-request checklist unless a workflow is
added deliberately later.

## Prerequisite

`Dorely/Lorekeeper` is private. GitHub currently returns `403` for both branch
protection and repository rulesets and says the account must upgrade to GitHub
Pro or make the repository public. Keep the repository private and upgrade the
owner account to GitHub Pro before applying the settings below.

## General pull request settings

Open **Settings > General > Pull Requests** and enable:

- Allow auto-merge.
- Always suggest updating pull request branches.

Leave **Automatically delete head branches** disabled so the reusable remote
work branch remains available after merge.

Enable merge commits and disable squash and rebase merging. The repository
reuses one work branch across accumulation cycles, so `main` must retain the
submitted head as an ancestor. Squash and rebase merging replace that ancestry
and force the reusable branch into divergence.

## Actions permissions

Open **Settings > Actions > General**:

- Allow the actions needed by the repository workflows.
- Set workflow permissions to **Read repository contents and packages**.
- Leave **Allow GitHub Actions to create and approve pull requests** disabled.

## Protect `main`

After GitHub Pro is active, open **Settings > Branches** and add a branch
protection rule for `main` with these settings:

- Require a pull request before merging.
- Require 1 approval.
- Dismiss stale pull request approvals when new commits are pushed.
- Require approval of the most recent reviewable push.
- Require conversation resolution before merging.
- Require branches to be up to date before merging.
- Include administrators / do not allow bypassing the rule.
- Do not allow force pushes.
- Do not allow deletions.

If a pull-request validation workflow is added later, run it on `main` first,
then require its exact successful check here. GitHub may not offer a status check
in branch settings until that check has run recently.

Do not enable code-owner review unless a deliberate `CODEOWNERS` policy is added
later. The repository currently requires one independent review, not ownership
by path.

## Verify the protection

Use a disposable branch and pull request to confirm:

1. A direct push to `main` is rejected, including from an administrator.
2. The pull request cannot merge without one approval.
3. Pushing another commit dismisses the old approval and requires approval from
   someone other than the latest pusher.
4. An unresolved review conversation blocks merge.
5. The pull request uses a merge commit and the reusable remote work branch
   remains available to fast-forward to the new `main`.

Keep the test change trivial and remove it through the same reviewed pull-request
workflow rather than bypassing the protection.
