# Privacy and local data

Lorekeeper is a local desktop application. The SQLite database stores projects,
manuscripts, sources, images, settings, conversations, jobs, provider API keys,
and OAuth tokens. Packaged builds use per-user application data outside the
installation; development builds use a repository-local database. SQLite and
the browser/Electron local storage are not encrypted by Lorekeeper.

Unsent assistant drafts and manuscript conflict recovery copies can also exist
in browser/Electron local storage, outside database backups and project exports.
Development-only diagnostic logs are bounded and redacted, but can still
contain project and tool detail. Review them before sharing. Release builds do
not write those development logs.

There is no Lorekeeper-operated cloud workspace or analytics service. The app
contacts services when you select their capabilities:

- AI, embedding, search, and image providers receive the content required for
  the requested operation. Background jobs can continue operations you queued.
  Context can include manuscripts, canon, writing samples, sources, and images.
  Provider terms, retention policies, and charges apply independently.
- OAuth opens the provider's browser authorization page and stores the returned
  credential in the provider-owned local persistence boundary. The bundled
  GitHub OAuth client ID is a public application identifier, not a credential.
- GitHub version-history synchronization is opt-in per remote. An attached
  remote can receive creative snapshots, sources, images, and fonts; subsequent
  checkpoints queue automatic pushes. Review remote visibility and asset rights
  before attachment. Credentials, chats, jobs, and derived indexes are excluded
  from those snapshots.
- **Check for updates** in Free builds queries GitHub release metadata. A selected
  download action opens the release in your browser. Store builds use Microsoft
  Store's update mechanism instead. Development builds do not check for updates.

Keep local-data and project backups somewhere you control. Project exports and
Git snapshots are creative-content formats, not full database or credential
backups. Deleting a local project does not delete its remote GitHub repository.
Removing a remote attachment leaves its existing remote content intact.

This repository does not collect your app data. Do not put private projects,
databases, keys, tokens, or unreviewed diagnostic output in public issues or pull
requests. [README.md](README.md#local-data) explains recovery and local storage;
[SECURITY.md](SECURITY.md) describes private vulnerability reporting.
