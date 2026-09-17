# Contributing

Use a branch/worktree and open a pull request. Keep one coherent change per PR and describe behavior, validation and limitations. Contributions are under AGPL-3.0-only.

```sh
dotnet restore ArcScope.slnx --locked-mode
dotnet run --project eng/ArcForges.Repository -- hooks
dotnet run --project eng/ArcForges.Repository -- check
dotnet format ArcScope.slnx --verify-no-changes --no-restore
dotnet build ArcScope.slnx -c Release --no-restore
dotnet test --project tests/ArcForges.ArcScope.Tests/ArcForges.ArcScope.Tests.csproj -c Release --no-build
```

`dotnet format ArcScope.slnx --no-restore` fixes C# formatting. Hooks are local to the checkout using Git worktree configuration. Pre-commit checks text and whitespace; pre-push restores locked dependencies, builds and runs deterministic tests. Hosted CI is authoritative even if hooks are not installed. Hooks do not call the live backend.

Change direct dependency versions only in `Directory.Packages.props`. After an intentional dependency change, run `dotnet restore ArcScope.slnx --force-evaluate -p:RestoreLockedMode=false`, inspect every lock diff, and rerun locked restore and validation. Updating an SDK also requires refreshing implicit runtime/compiler entries in locks. Do not hand-edit content hashes or disable audit/locked restore to make a bot PR green.

Review upstream licence changes when updating dependencies. `third-party/` preserves primary licence texts missing from NuGet packages, with pinned source commits and hashes. Update those records if the corresponding upstream terms change; preserve their bytes. Native package notices embedded by upstream are collected automatically during staging.

Native release/live validation is described in [development](docs/development.md). No secrets are required for Hello. Do not place credentials or personal data in fixtures, logs or screenshots. Report security issues privately using [SECURITY](SECURITY.md).
