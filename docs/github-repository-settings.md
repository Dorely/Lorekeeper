# GitHub repository settings

This is the maintainer checklist for making `main` a reviewed, pull-request-only
branch. Repository instructions and pull-request validation already assume these
controls are active.

## Prerequisite

`Dorely/Lorekeeper` is private. GitHub currently returns `403` for both branch
protection and repository rulesets and says the account must upgrade to GitHub
Pro or make the repository public. Keep the repository private and upgrade the
owner account to GitHub Pro before applying the settings below.

## General pull request settings

Open **Settings > General > Pull Requests** and enable:

- Allow auto-merge.
- Always suggest updating pull request branches.
- Automatically delete head branches.

Squash merging is the recommended merge method because the protected branch
will require linear history. Rebase merging may remain available. Disable merge
commits if GitHub does not do so automatically when linear history is required.

## Actions permissions

Open **Settings > Actions > General**:

- Allow the actions needed by the repository workflows.
- Set workflow permissions to **Read repository contents and packages**.
- Leave **Allow GitHub Actions to create and approve pull requests** disabled.

## Protect `main`

After GitHub Pro is active, open **Settings > Branches** and add a branch
protection rule for `main` with these settings:

First open **Actions > Pull request validation**, run the workflow once on
`main`, and confirm **Repository gate** succeeds. GitHub may not offer a status
check in branch settings until that check has run recently.

- Require a pull request before merging.
- Require 1 approval.
- Dismiss stale pull request approvals when new commits are pushed.
- Require approval of the most recent reviewable push.
- Require conversation resolution before merging.
- Require status checks to pass before merging.
- Require branches to be up to date before merging.
- Select the required check **Repository gate** from the **Pull request
  validation** workflow.
- Require linear history.
- Include administrators / do not allow bypassing the rule.
- Do not allow force pushes.
- Do not allow deletions.

Do not enable code-owner review unless a deliberate `CODEOWNERS` policy is added
later. The repository currently requires one independent review, not ownership
by path.

## Verify the protection

Use a disposable branch and pull request to confirm:

1. A direct push to `main` is rejected, including from an administrator.
2. The pull request cannot merge before **Repository gate** succeeds.
3. The pull request cannot merge without one approval.
4. Pushing another commit dismisses the old approval and requires approval from
   someone other than the latest pusher.
5. An unresolved review conversation blocks merge.
6. The remote branch is deleted after merge.

Keep the test change trivial and remove it through the same reviewed pull-request
workflow rather than bypassing the protection.
