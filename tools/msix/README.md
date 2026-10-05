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
the exact package, runs SDK MakeAppx semantic validation without `/nv`, unpacks
it, verifies contents and all notice hashes, and leaves it unsigned. It does not
create certificates, install a package, contact Partner Center, or submit a
listing. See [store-listing.md](store-listing.md) for prepared copy, free pricing,
capability explanations, privacy link, screenshot/trailer requirements, and the
later owner-operated upload.

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
Windows 11 declares the scoped Lorekeeper application-data preservation rule;
Windows 10 uses the filesystem-virtualization fallback. These are manifest
declarations, not installed-system evidence.

On 2026-10-05, the fresh Store closure, MakeAppx pack/unpack, license retention,
local CMS verification, and ephemeral certificate/key cleanup were exercised.
Installation, upgrade, uninstall/data retention, Windows 11, Store-managed
updates, production identity, and Store certification remain unperformed. Run
them only in an explicitly authorized disposable Windows profile and record the
exact package version and target OS. No submission or Store listing was created.
