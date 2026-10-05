# Contributing to Lorekeeper

Read [VISION.md](VISION.md), [the architecture map](docs/architecture.md), and the
chapters routed to your change before implementation. [AGENTS.md](AGENTS.md)
records the repository's working, verification, and documentation rules.

Lorekeeper is source available under the two alternatives in [LICENSE](LICENSE).
By submitting a contribution, you confirm that you have the right to contribute
it and offer it under both of those same licenses. Third-party code or assets
must retain their own license and provenance and pass the distribution policy;
do not copy material whose redistribution rights are uncertain.

For a bug, open an [issue](https://github.com/Dorely/Lorekeeper/issues) with the
app version, operating system, reproducible steps, expected behavior, and a small
synthetic example. Keep manuscripts, databases, provider credentials, OAuth
tokens, and private diagnostic detail out of public issues and pull requests.
Use the private reporting route in [SECURITY.md](SECURITY.md) for vulnerabilities.

Discuss a substantial feature before building it. The current release effort is
preparation and validation; its scope does not include new product features.
Keep pull requests focused, trace callers and consumers, update the owning
documentation, and explain the resulting behavior and verification.

Build with the tools and commands in [README.md](README.md). The final change
must pass `dotnet build Lorekeeper.sln` and
`dotnet test Lorekeeper.Tests/Lorekeeper.Tests.csproj`. Run `cargo test` in
`Lorekeeper.Press` when Press sources change and complete the owning chapter's
impact-specific static checks.

Automated tests are restricted to the data-safety core in `Lorekeeper.Tests` and
the Press conformance matrix in `Lorekeeper.Press`. Do not add other test suites
or one-off test harnesses. Interactive startup, UI, Electron, provider, Word,
and target-platform exercises require the maintainer's explicit authorization;
compilation does not establish those integration claims. State any unperformed
validation in the pull request.

Use synthetic disposable data for authorized manual checks. Using the
maintainer's current app requires explicit authorization for that environment
and the exact synthetic exercise. Do not alter working projects, history, or
credentials, or start a competing host on an occupied debug port. Terminate
every process started for validation.
