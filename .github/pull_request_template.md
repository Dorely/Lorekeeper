## Summary

<!-- What changed, and why? -->

## Verification

- [ ] I fetched `origin` immediately before final verification and this branch includes the latest `origin/main`.
- [ ] I inspected the complete branch diff and excluded unrelated changes.
- [ ] `dotnet build Lorekeeper.sln`
- [ ] `dotnet test Lorekeeper.Tests\Lorekeeper.Tests.csproj`
- [ ] `cargo fmt --check` in `Lorekeeper.Press`
- [ ] `cargo clippy --all-targets -- -D warnings` in `Lorekeeper.Press`
- [ ] `cargo test --locked` in `Lorekeeper.Press`
- [ ] I ran every additional check required by the routed architecture chapters.

## Documentation and risk

- [ ] Current architecture and user-facing documentation are updated, or no update is required.
- [ ] Superseded names, paths, registrations, and documentation were removed.
- [ ] Unperformed integration or platform validation is called out below.

<!-- Note migration, security, release, platform, or follow-up risks here. -->
