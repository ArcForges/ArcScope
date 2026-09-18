# Project licence boundary (WP00.02)

All four current ArcScope projects, including tests and repository tooling, declare
`PackageLicenseExpression=AGPL-3.0-only` and `LicenceBoundary=AGPL`. The closed
inventory is `eng/policy/licence-boundary.json`, following the accepted
[Design profile](https://github.com/ArcForges/ArcForges-Design/blob/6ba885ad38dd71de532c74d7b69f439d01d19a0a/docs/architecture/01-solution-and-project-layout.md#41-project-declaration-and-verification-profile).

The existing C# repository `check` command compares the actual Git inventory with
that data, reads every declaration and imported property file, checks project
reference containment and validates the locked first-party package identities.
An added project, missing declaration, inconsistent pair, escaped reference or
unknown first-party package fails the check. Dependency licences remain separate
from the owner's SPDX declaration; the existing distribution notices are retained.

`Directory.Build.targets` also checks effective MSBuild properties before build
and pack, so an imported or command-line override cannot bypass source checks.
The inventory test runs in every native CI job. It writes the source commit,
dirty state, boundary and complete project list to
`artifacts/evidence/licence-boundary.json`, retained with the existing UI evidence.
Adversarial C# tests exercise declaration, inventory, import, reference and lock
failures. The existing locked restore, format, build, tests, five-RID Native AOT
UI/live-Cloud checks and immutable candidate verification remain required.

This change preserves published dependency versions, application identity,
signing behavior and product scope. A policy pass does not prove product readiness
or close a later commercial-release gate. Current runs and post-merge assets must
be verified for their exact source commit.
