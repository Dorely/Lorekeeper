# Lorekeeper Privacy Policy

Effective date: October 13, 2026

This policy covers the Lorekeeper desktop application published by Dorely,
including the Microsoft Store edition and the builds released on GitHub.

## Summary

Lorekeeper does not collect, sell, or share your personal information. The
developer operates no Lorekeeper account system, cloud workspace, analytics,
telemetry, or advertising service, and receives none of your projects or usage
data. Your work stays on your computer unless you choose to connect a
third-party service.

## Information stored on your computer

Lorekeeper stores the following in a local SQLite database and Electron
browser storage under your Windows user profile:

- projects, manuscripts, outlines, canon, sources, images, fonts, and settings;
- assistant conversations, background jobs, and unsent drafts;
- API keys and OAuth tokens for providers you connect;
- local version-history repositories and recovery backups.

Lorekeeper does not encrypt this data itself. Anyone with access to your
Windows account can read it. Release builds do not write diagnostic logs.

## Third-party services you choose to use

Lorekeeper contacts an outside service only when you configure and use one:

- **AI, embedding, search, and image providers** (for example OpenAI) receive
  the content needed for the request you make. Depending on the feature, that
  can include manuscript text, canon, writing samples, sources, and images.
  Each provider's own privacy policy, retention terms, and charges apply.
- **OAuth sign-in** opens the provider's authorization page in your browser,
  and Lorekeeper stores the resulting credential locally.
- **GitHub version-history sync** is opt-in for each remote repository you
  attach. Creative snapshots, sources, images, and fonts are pushed to that
  repository. Credentials, chats, jobs, and search indexes are never included.
- **Update checks** in GitHub-released builds read public GitHub release
  information. The Microsoft Store edition receives updates through the Store.

The developer receives no data from any of these services.

## Children

Lorekeeper is a general-audience writing tool and does not knowingly collect
information from anyone, including children.

## Retention and deletion

Your data stays on your computer until you delete it. Deleting a project in
Lorekeeper removes it from the working database; earlier recovery backups can
retain a copy until they are pruned. In the Microsoft Store edition,
Windows removes Lorekeeper's local data when you uninstall the app, so export
projects (`.lorekeeper` archives) or sync them to GitHub first if you want to
keep them. Deleting a local project does not delete a GitHub repository you
attached. Data you sent to a third-party provider is subject to that
provider's retention policy.

## Security

Keep your Windows account protected, and treat project exports and backups as
private files. Report vulnerabilities privately as described in
[SECURITY.md](https://github.com/Dorely/Lorekeeper/blob/main/SECURITY.md).

## Changes to this policy

Updates to this policy are published at this address and in the Lorekeeper
source repository with a new effective date.

## Contact

Questions about this policy can be raised through
[GitHub issues](https://github.com/Dorely/Lorekeeper/issues). Do not post
private projects, databases, keys, or tokens in public issues.
