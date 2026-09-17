# Automatic portable releases

No release API key or publishing account is required. CI uses the repository-scoped `GITHUB_TOKEN`; only the release job receives `contents: write`. Public Hello needs no secret. Repository security features and the required `Verify` check are configured in GitHub settings, not injected into a client binary.

1. A PR runs deterministic tests, formatting/locked restore, secret/dependency review, C#/Actions CodeQL and five native builds: Windows x64/ARM64, Linux x64, macOS x64/ARM64.
2. Each native job publishes the self-contained AOT directory, includes notices, prepares its portable layout, and runs the exact staged executable's native UI/live smoke. macOS ad-hoc signing happens before testing and archiving.
3. Only after that success does it create a candidate archive, SHA-256 checksum, source/version/RID manifest and evidence. Upload artifact names include the run ID and attempt. Failed jobs retain diagnostic evidence but cannot produce a release candidate.
4. `Verify` requires all platform and security jobs and downloads all five archives to exercise the aggregate identity/hash/evidence check before merge. A successful **push to main** alone enables automatic publication. Manual/scheduled runs and PRs validate only.
5. The release job downloads candidates from that same workflow run, verifies all five identities, hashes and evidence, then uploads those exact archives without rebuilding. Version is `0.1.0-ci.<run_number>.<run_attempt>`. Re-running uses a new immutable version. It creates a draft first, uploads all assets, then makes the prerelease visible; a failed upload leaves a draft rather than a complete-looking release.

Download `arcscope-VERSION-RID.zip` or `.tar.gz` together with its `.sha256`. Evidence and package inventory describe what was exercised; a checksum detects corruption but is not a trusted code signature. Do not overwrite a published archive or repoint its Git tag. Fix the source and publish a new successful main run. Consumers can roll back by extracting an older complete version into a separate directory; this Hello version has no persisted user data or migration.

The five candidates use native GitHub hosts, not cross-compilation as a substitute for execution. A live service outage blocks publication and preserves its error evidence. First release publication itself is verified only after the PR is merged; a green PR does not demonstrate a main-branch release has already occurred.

The current distributions are portable development builds. Authenticode, Developer ID/notarization, installers, app-store delivery and update signing require separate credentials and implementation before trusted public product distribution.
