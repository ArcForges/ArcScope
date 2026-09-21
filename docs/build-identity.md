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

Preparation runs the real AOT binary and verifies its identity against independent
Git/run/release and restored dependency inputs. Portable archives retain that report;
verification rejects internal changes even with recomputed outer hashes. Existing
five-native-host UI and live Cloud checks remain mandatory. `verify-assemblies`
inspects the actual PE metadata of the app, core, tests and C# repository tool.

The source adaptation and immutable predecessor are recorded in
`eng/provenance/records/arcnotes-provenance-tools-r2.json`. Tests exercise independent
mutations of all nine source kinds, malformed/aliased/missing sources and tampered
build/archives. Synthetic declarations validate mechanisms only. Public artifacts
and runtime results must be verified separately for ArcScope; these foundation
reports do not establish commercial instrument workflows or future compatibility.
