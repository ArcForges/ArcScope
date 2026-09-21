# WP02.04 build identity

Run `ArcScope --build-info --evidence <path.json>` to obtain offline support
information from the compiled application. The command executes before host/UI
initialization and reads embedded inputs, without consulting runtime CI variables.

The source catalog declares exactly nine independent axes. Application release,
restored Contracts schema/descriptor and resolved package-lock coordinates have
separate sources. Unsupported future capability, format, storage, policy and
extension implementations identify their responsible owners explicitly. Third-party
native dependencies do not invent a first-party C ABI version.

Published Build.Policy 1.0.0-ci.20.1 stamps every owned assembly; owner targets add
actual dirty state and reject source mismatches. The build identity contains the
full commit, local/CI kind, run/attempt, pipeline URL and Git source timestamp.
Contracts remains pinned to 1.0.0-ci.36.1, whose Hello contract major is 1.

The repository tool statically reads actual app/core/test/tool PE metadata once.
Preparation writes `build-identity.json` from the reviewed Git/run/release, restored
dependency and committed source inputs without launching the AOT app. This packaged
build-input receipt is not runtime execution evidence. The app's explicit local
`--build-info` command remains available for relevant support diagnostics.
Publication checks candidate identity and legal/source integrity once. CI has three
Windows/Linux compilation targets, no macOS/UI/live execution and no routine public
archive download or runtime verification cycle.

The source adaptation and immutable predecessor are recorded in
`eng/provenance/records/arcnotes-provenance-tools-r2.json`. Tests exercise independent
mutations of all nine source kinds, malformed/aliased/missing sources and tampered
build/archives. Synthetic declarations validate mechanisms only. Any optional local runtime observation must identify the actual artifact tested;
these foundation reports do not establish commercial instrument workflows or future compatibility.

The r3 provenance successor retains r1/r2 and records the CI/release reduction without changing the original runtime implementation or dependency pins.
