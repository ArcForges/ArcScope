# ArcScope contributor and agent instructions

- Read `docs/bootstrap-plan.md`, `README.md` and the relevant source before changes. Collect concrete failures, decide a bounded fix, then implement and verify it.
- Keep this repository a C# desktop consumer. Use published, exact NuGet dependencies and committed locks. Do not add sibling source references or submodules.
- Keep AGPL-3.0-only headers and the existing licence. Do not import reference code under incompatible terms.
- Preserve Native AOT, normal TLS validation, `/api` routing, RPC deadlines, cancellation and user-visible failures. No fake success, credential in a desktop binary, blanket trimming suppression or automatic application retries.
- Keep the production entry point offline until a user action. Runtime tests are explicit local opt-in only when the affected behavior needs them and the existing environment supports them.
- Follow the [accepted CI and local validation policy](https://github.com/ArcForges/ArcForges-Design/blob/47db6670a727317939b91245e8c0b288834acf99/docs/assurance/ci-and-local-validation-policy.md). No macOS CI, device/emulator/GUI/browser E2E, live service or installed-package consumer execution is permitted in any hosted workflow or nested default build/publish command.
- CI compiles and packages Windows x64/ARM64 and Linux x64. Run platform-independent static checks and offline unit tests once. Preserve dependency locks, necessary signing, source/licence provenance and one candidate integrity check at the publication handoff.
- Do not routinely download published assets, compare their hashes/members or run another installation/runtime verification cycle. A concrete integrity defect or explicit user request is required for a scoped diagnostic download.
- Validate only affected behavior once. Do not install or reinstall vcpkg, SDKs, emulators or toolchains to expand coverage. Hooks must not rebuild or test implicitly. Serialize CPU-heavy local work and reuse existing caches.
- Never describe unrun local/macOS/runtime checks as passed. Historical evidence is not a command to repeat it. Documentation-only changes need consistency review, not product builds.
- Use the normal network; no proxy 7890, other proxy configuration, wsl.exe or WSL wrappers. Stop and report the exact failing operation on a network failure without retries.
- Use retained worktrees/branches and PR titles prefixed with the current work package/substep. Review the full latest PR head and merge only after applicable reduced CI succeeds when merging is user-authorized. Post-merge checks stop after expected commit, required publication result and clean primary fast-forward; do not begin another validation cycle.
