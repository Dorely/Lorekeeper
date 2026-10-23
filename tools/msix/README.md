# Free Microsoft Store MSIX preparation

`Build-WindowsMsix.ps1` uses the full-trust desktop model (`runFullTrust`,
`packagedClassicApp`, `mediumIL`). It does not claim AppContainer confinement.
The [MakeAppx guidance](https://learn.microsoft.com/en-us/windows/msix/package/create-app-package-with-makeappx-tool)
and [desktop packaging model](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion)
describe this boundary. Store signing removes the need for a personal production
code-signing certificate when submitting this MSIX through Partner Center.

Build a Store-channel closure first:

```powershell
.\scripts\build-windows-release.ps1 -KeepUnpacked -DistributionChannel Store
```

Copy `store-identity.example.json` to ignored `store-identity.json` and fill the
exact Partner Center `packageIdentityName`, `packageIdentityPublisher`,
`publisherDisplayName`, and reserved `displayName`. No sample identity is valid
for production. Then prepare the unsigned submission package:

```powershell
.\tools\msix\Build-WindowsMsix.ps1 -IdentityPath .\tools\msix\store-identity.json
```

The default output is `.artifacts/msix/store/Lorekeeper-Store-<version>-x64.msix`
with `msix-package.json`. The script validates immutable Store metadata, stages
the exact package, renders every logo from `icon-512.png` at scale-100 to
scale-200 plus `Square44x44Logo` target sizes 16-256 in plated, unplated, and
light-unplated forms, indexes only `Assets` into `resources.pri` with
[priconfig.xml](priconfig.xml), runs SDK MakeAppx semantic validation without `/nv`, unpacks
it, verifies contents and all notice hashes, and leaves it unsigned. It does not
create certificates, install a package, or contact Partner Center. See
[store-listing.md](store-listing.md) for prepared copy, free pricing, capability
explanations, privacy link, and screenshot/trailer requirements.

## Store submission

`Submit-WindowsMsix.ps1` uploads the prepared package to product `9N3QQPF401L5`
through the [Microsoft Store Developer CLI](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/commands)
(`msstore`, PowerShell 7.5 or later). One-time owner setup: associate a Microsoft
Entra tenant with the Partner Center account, create a Partner Center Entra
application with the Manager role, add a key, then run `msstore reconfigure` with
the tenant, seller, and client IDs and paste the key at its prompt. The key lives
only in Windows Credential Manager; never place it in files, arguments, or logs.
It expires after at most two years; rerun `msstore reconfigure` with a new key.

```powershell
.\tools\msix\Submit-WindowsMsix.ps1 -ReleaseNotes '' -CheckOnly
.\tools\msix\Submit-WindowsMsix.ps1 -ReleaseNotes 'Plain-text What''s new'
.\tools\msix\Submit-WindowsMsix.ps1 -ReleaseNotes 'Plain-text What''s new' -Submit
```

`-CheckOnly` reads the current submission only: it requires that nothing is
pending and that every live package version is lower than the source mapping;
`-Version` previews a planned version. A real run also requires
`msix-package.json` to describe the current source version and the package
SHA-256 to match. It then creates a draft with `msstore publish --noCommit`,
confirms the draft holds exactly that package with no equal or newer live
package, replaces every listing's release notes (at most 1,500 characters; empty
clears them), and re-reads the draft. Without `-Submit` the draft stays in
Partner Center for review; `-Submit` commits it for certification, which
completes asynchronously. The run records `store-submission.json` beside the
package. A failure after upload leaves the draft in Partner Center; finish or
delete it there before rerunning. The release driver's `-StoreSubmission`
option builds and submits through this script; see the repository README.

Channel and informational-version checks read bounded PE metadata without
loading application code. Inspection handles close on success and failure, so
later packaging commands can replace the input DLL in the same PowerShell
session.

For disposable package-structure evidence with no real Store identity:

```powershell
.\tools\msix\Build-WindowsMsix.ps1 -LocalValidation -CheckOnly
.\tools\msix\Build-WindowsMsix.ps1 -LocalValidation
```

`-LocalValidation` and `-IdentityPath` are mutually exclusive. The former uses a
separate identity, produces `.artifacts/msix/local-validation/`, signs with one
ephemeral current-user certificate, verifies embedded CMS integrity and exact
signer, and removes both certificate and private key. It does not trust the
certificate or install the package. `-CheckOnly` validates the existing Store
closure without package or certificate operations. Optional
`-WindowsUnpackedDirectory` and `-OutputDirectory` must remain within the
repository's intended generated-output boundary.

MSIX versions use `major+1.minor.patch.0`: source `1.0.0` becomes `2.0.0.0`,
preserving monotonic updates after the earlier feasibility versions.
The package declares no `unvirtualizedResources` capability; certification
denied it on 2026-10-13. Standard MSIX filesystem virtualization redirects
`%LocalAppData%\Lorekeeper` writes into package-private storage, which persists
across Store updates and is removed on uninstall. Users keep data across
reinstalls through `.lorekeeper` project exports or GitHub history sync. This is
the manifest contract, not installed-system evidence.

On 2026-10-05, the fresh Store closure, MakeAppx pack/unpack, license retention,
local CMS verification, and ephemeral certificate/key cleanup were exercised.
Store certification passed on 2026-10-14 for package `2.0.1.0` (source
`1.0.1`, production identity `Dorely.Lorekeeper`), and the listing is live as
[product 9N3QQPF401L5](https://apps.microsoft.com/detail/9N3QQPF401L5). The first submission, `2.0.0.0`, was rejected for
the privacy URL and the `unvirtualizedResources` request; Partner Center keeps
that package name reserved, so every resubmission needs a higher source version.
Installation, upgrade, uninstall/data retention, Windows 11, and Store-managed
updates on owner hardware remain unrecorded. Run them only in an explicitly
authorized disposable Windows profile and record the exact package version and
target OS.
