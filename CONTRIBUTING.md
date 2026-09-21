# Contributing

Use a branch/worktree and open a pull request. Keep one coherent change per PR and describe behavior, validation and limitations. Contributions are under AGPL-3.0-only.

```sh
dotnet restore ArcScope.slnx --locked-mode
dotnet format ArcScope.slnx --verify-no-changes --no-restore
dotnet build ArcScope.slnx -c Release --no-restore
dotnet run --project eng/ArcForges.Repository -c Release --no-build -- check
dotnet test --project tests/ArcForges.ArcScope.Tests/ArcForges.ArcScope.Tests.csproj -c Release --no-build --filter-not-class '*.TransportTests'
```

`dotnet format ArcScope.slnx --no-restore` fixes C# formatting. Hooks are local to the checkout using Git worktree configuration. Pre-commit and pre-push check Git whitespace only. Run the relevant commands above explicitly once for source changes; documentation-only changes need consistency review. CI repeats no GUI/live/runtime scenarios and runs offline unit checks on one Linux host.

Change direct dependency versions only in `Directory.Packages.props`. After an intentional dependency change, run `dotnet restore ArcScope.slnx --force-evaluate -p:RestoreLockedMode=false`, inspect every lock diff, and rerun locked restore and validation. Updating an SDK also requires refreshing implicit runtime/compiler entries in locks. Do not hand-edit content hashes or disable audit/locked restore to make a bot PR green.

Review upstream licence changes when updating dependencies. `third-party/` preserves primary licence texts missing from NuGet packages, with pinned source commits and hashes. Update those records if the corresponding upstream terms change; preserve their bytes. Native package notices embedded by upstream are collected automatically during staging.

Optional local native and loopback transport validation is described in [development](docs/development.md); it is not a CI or publication prerequisite. No secrets are required for Hello. Do not place credentials or personal data in fixtures, logs or screenshots. Report security issues privately using [SECURITY](SECURITY.md).
