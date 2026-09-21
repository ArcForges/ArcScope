# Automatic portable releases

CI follows the [accepted validation policy](https://github.com/ArcForges/ArcForges-Design/blob/47db6670a727317939b91245e8c0b288834acf99/docs/assurance/ci-and-local-validation-policy.md). No macOS or application runtime test runs in any workflow.

1. PRs compile Windows x64/ARM64 and Linux x64 Native AOT candidates. Formatting, source/licence checks, static assembly metadata inspection and offline unit tests run once on Linux. Secret/dependency review and C#/Actions CodeQL remain required.
2. Staging includes the complete executable directory, dependency/legal notices and build-input identity receipt without launching the app. Packing creates each archive, source/version/RID manifest and SHA-256 sidecar. Smoke results and screenshots are not release inputs.
3. The required `Verify` job checks applicable job outcomes only. It does not download or rescan artifacts. PR, manual and scheduled runs never publish.
4. A successful main push promotes the same run's three candidates. The release job retrieves them once and performs one identity, archive-integrity and licence/provenance check at this publication handoff. It does not rebuild or execute them.
5. The release job creates a draft `v0.1.0-ci.<run_number>.<run_attempt>`, uploads three archives, three checksum sidecars and `arcscope-verification.tar.gz` containing build manifests, then exposes the prerelease. `Verify publication` requires the publishing job to succeed.

The repository-scoped `GITHUB_TOKEN` grants `contents: write` only to publication. The explicit main publication condition tolerates the intentionally skipped PR-only dependency review while requiring `Verify` success. Do not overwrite published archives or repoint tags; do not rerun publication to manufacture validation evidence.

Provider publication success and the expected commit/version close post-merge verification. Do not routinely download public assets, compare hashes/members, install them or rerun consumers. No live service availability is a release gate. A green build is not a native UI/live test result.

Current automated releases contain Windows and Linux portable development distributions only. Local macOS source/build support is retained; this workflow produces no macOS artifact or runtime claim. Windows binaries are unsigned. Installers, app stores, trusted OS signing and updates are separate delivery features.

A `.sha256` detects corruption; it is not a trusted code signature. Source is the release's exact tag/commit. Existing complete releases remain available as historical artifacts.
