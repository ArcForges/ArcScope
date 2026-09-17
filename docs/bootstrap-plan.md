# ArcScope desktop Hello bootstrap

## Source and bounded scope

Copy the complete tracked ArcChat foundation at [merged commit 818ca61b106eb373de94622125a9a54e5a3644d5](https://github.com/ArcForges/ArcChat/commit/818ca61b106eb373de94622125a9a54e5a3644d5), then adapt product identity for ArcScope (instrument acquisition and analysis). The target started at 35cf1bba50bd07f4e81a5f859eb83a5afdbba3fa with only the same AGPL licence. This is a Hello bootstrap, not implementation of the product's full business workflows.

The requested procedure is copy and rename only, without local compilation. The common .NET 10 / C# 14 / Avalonia stack, exact published packages and lock hashes, transport behavior, five-RID CI, tests, hooks, security tooling and verified main-push release order come from that source revision.

## Copy plan

1. Create an isolated worktree and branch from the target's origin/main. Export only Git-tracked files from the immutable source revision; exclude worktrees, build outputs, caches and credentials.
2. Rename solution/project paths, namespaces, application class/title, assembly/executable names, macOS bundle identifier, repository links, package/artifact names and Hello smoke expectations. Preserve Cloud and Contracts endpoint/protocol identities and dependency versions. Adapt the product description.
3. Replace the source project's historical implementation record with this record. Preserve upstream licence text bytes and hashes; do not transfer source CI or local execution results to this project.
4. Verify every copied file against the deterministic rename, parse structured files, resolve solution/project references, inspect lock identity and dependency equality, check upstream hashes, run whitespace/workflow syntax checks, and scan for stale product names.
5. Copy repository maintenance/security settings, commit and open a PR. Preserve the hook files but bypass hooks that would build or test during this requested copy-only operation. GitHub CI runs the inherited native/platform/live checks independently. Do not merge or publish from this task.

## Evidence and limits

Local validation covers file completeness, deterministic identity replacement, structured-file/reference consistency, unchanged third-party dependencies and licence hashes, workflow syntax and Git whitespace only. No local restore, build, test, native execution or live request is performed for ArcScope. ArcChat's earlier results remain source-project evidence. Consult this repository's PR checks for its own hosted evidence; a copied pipeline is not proof of a product build or release.

Merging a successful PR to main triggers automatic versioning and publication of the five verified portable candidates. Public Hello requires no new credential. These development distributions retain the source template's Windows unsigned/macOS ad-hoc signing status.
