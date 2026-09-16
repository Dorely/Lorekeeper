# Local Windows MSIX feasibility input

`Package.appxmanifest.xml` deliberately packages Lorekeeper as a full-trust
desktop application: it declares `runFullTrust`, `packagedClassicApp`, and
`mediumIL`. It is a local test identity, not a Microsoft Store identity, and it
does not claim AppContainer confinement.
[Microsoft's MakeAppx guidance](https://learn.microsoft.com/en-us/windows/msix/package/create-app-package-with-makeappx-tool)
and [desktop packaging model](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion)
define this local feasibility boundary.

`Build-M0MsixFeasibility.ps1` copies the source-controlled
[`Lorekeeper/wwwroot/branding/icon-512.png`](../../Lorekeeper/wwwroot/branding/icon-512.png)
into the three manifest-referenced PNG locations in its ignored package staging
directory. The copied assets and package are generated; the application icon is
the source-controlled package asset input. A future Store submission must replace
the local test identity and supply Store-reserved identity/asset evidence.

The script requires a `Store`-channel `win-unpacked` build and produces an
ignored, signed local test package. It verifies the embedded CMS integrity and
that the signer is its exact ephemeral current-user test certificate, then removes
that certificate. It does not modify the trusted-root store, so Windows
trust-chain acceptance is intentionally deferred with installation to the
separately authorized disposable test-profile session. It never installs the
package, creates a Store submission, or contacts a provider or GitHub.
