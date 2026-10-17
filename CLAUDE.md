# Pug.Sqetch

DevOps database schema upgrader. A **project** contains **plans** (units of schema change),
each plan contains **steps** with three SQL scripts (`deploy.sql`, `verify.sql`,
`rollback.sql`). Plans are grouped into **releases** that form a single linear lineage: every
release except the first names exactly one predecessor (`Dependency`), and a release may have
at most one dependant. Finalizing a release freezes it and its plans. The separate **sqetch-deploy** CLI owns
everything downstream of authoring: its `bundle` command packages a project's deployable plans
plus a manifest for deployment pipelines, and its `deploy` command consumes those bundles and
deploys them to a database through pluggable database drivers, journaling through SQL the
project maintainer authors.

## Commands

```sh
dotnet build                 # full solution
dotnet test                  # all test projects; must be green before any commit
dotnet run --project Pug.Sqetch.Cli -- <args>   # run the 'sqetch' CLI
dotnet run --project Pug.Sqetch.Deployment.Cli -- <args>   # run the 'sqetch-deploy' CLI
dotnet run --project Pug.Sqetch.Deployment.Cli -- bundle --project <dir> -o <bundle>
dotnet run --project Pug.Sqetch.Deployment.Cli -- deploy <bundle> --driver sqlite --sqlite-file db
dotnet run --project Pug.Sqetch.Deployment.Cli -- drivers parameters sqlite   # a driver's switches
pwsh Installers/build/build.ps1 -Stage <stage> ...   # release build; see RELEASING.md
```

## Solution layout

| Project | Role |
|---|---|
| `Pug.Sqetch.Models` (net8.0) | Plain records: definitions, criteria, `StepScripts`, `UserInfo` |
| `Pug.Sqetch.Abstractions` (net8.0) | Every contract, grouped by folder: root — `IProject`/`IReadOnlyProject`, `IScriptsStore`, domain exceptions; `ProjectInfoStore/` — `IProjectInfoStore`; `Bundling/` — `Bundle` model, `BundleMetadata` (the `META-DATA` entry), `IBundleType`/`IBundleWriter`/`IBundleLayout`, registry interfaces, bundling exceptions; `Deployment/` — `JournaledRelease`, deployment exceptions; `DatabaseDriver/` — `IDatabaseDriverFactory`/`IDatabaseDriver`/`IDatabaseTransaction`, `JournalingParameter`, registry interface, driver exceptions (based on `DatabaseDriverException`, not `DeploymentException`, so the driver contract does not depend on deployment) |
| `Pug.Sqetch` | The engine (`Project`): business rules, dependency ordering, semaphores |
| `Pug.Sqetch.ProjectInfoStores.FileSystem` | Git-friendly file store: `ProjectPaths`, sharding, plan index, `NameRules` |
| `Pug.Sqetch.Bundling.BundleTypes` | zip / tar / tar.gz / directory implementations + `RegisterBundleTypes()` |
| `Pug.Sqetch.Bundling.Layouts` | `DefaultBundleLayout` + `RegisterLayouts()` |
| `Pug.Sqetch.Bundling` | `BundleBuilder` (assembles from an `IReadOnlyProject`) and `BundlePackager` (writes `META-DATA`, then the layout) + registry implementations |
| `Pug.Sqetch.Cli.Common` (net10.0) | What both CLIs present with: `OutputFormat`/`OutputFormats`, `OutputSettings` (`-o|--output`), `ResultWriter` |
| `Pug.Sqetch.Cli.Commands` (net10.0) | Every command of both CLIs and the helpers they use: `Authoring/` (ns `…Cli.Commands.Authoring`) for 'sqetch' — commands, `ProjectSession` (also opened read-only by `bundle`), `NameValidation`, `OptionParsing`, `UserIdentity`, `Output/Rows`; `Deployment/` (ns `…Cli.Commands.Deployment`) for 'sqetch-deploy' — commands (`bundle` included), `BundlerIdentity`, `DeploymentSettings`, `DriverParameters`, `RemainingArguments`, `ConsoleDeploymentListener`, `DeployCliErrors`, `DeployExitCodes`. The only layer touching `ProjectPaths` for output |
| `Pug.Sqetch.Cli` | The 'sqetch' host: `Program`, the command tree (`SqetchApp.Configure`) and `CliErrors` |
| `Pug.Sqetch.Deployment` (net8.0) | `DeploymentEngine` (deploy flow), `StatementJournal`, `BundleValidator`/`ValidatedBundle`, driver registry, `IDeploymentListener` |
| `Pug.Sqetch.DatabaseDrivers.Ado` (net8.0) | `AdoDatabaseDriver`/`AdoDatabaseTransaction`/`AdoDialect`: the ADO.NET half every provider driver inherits |
| `Pug.Sqetch.DatabaseDrivers.Sqlite` | `SqliteDatabaseDriverFactory` (Microsoft.Data.Sqlite) + `RegisterSqliteDriver()` |
| `Pug.Sqetch.DatabaseDrivers.PostgreSql` | `PostgreSqlDatabaseDriverFactory` (Npgsql) + `RegisterPostgreSqlDriver()` |
| `Pug.Sqetch.Deployment.Cli` | The 'sqetch-deploy' host: `Program`, the command tree and default composition (`SqetchDeployApp`, the only project referencing the concrete drivers), `TypeRegistrar` DI seam |
| `Pug.Sqetch.Tests*` | xunit test projects, one per tier |
| `Installers/` | Release packaging (GPL): `Publish.props` (self-contained single-file when a RID is set, imported by both hosts), `build/` (`build.ps1` + `Common.ps1` + one of `Windows.ps1`/`Linux.ps1`/`MacOS.ps1`), `Wix/` (MSIs), `nfpm/` (deb/rpm), `chocolatey/`, `smoke/` (project and bundle fixtures + `New-SmokeBundle.ps1`) |

## Architecture rules

- **Layering**: the store filters, the engine orders, the CLI presents. Ordered read APIs
  (`GetReleases`, `GetPlans`, `GetSteps`) return dependency-chronological order — never
  re-sort downstream. Only the CLI may use `ProjectPaths` (for printing paths).
- **Storage is git-legibility-first**: state changes move folders (plan → release folder on
  assignment; release → its shard on finalization). Only *finalized* releases are sharded;
  unfinalized ones live at `releases/<name>/`. `ReleaseDirectory` probes both locations —
  never scan `releases/` (the plan index and `EnumerateReleaseDirectories` exist for that).
- **The project format is versioned**: `sqetch-project.json` starts with `format`
  (`FileSystemProjectStores.Format`, absent = `1`), and opening a project of a higher or
  non-positive format throws `UnsupportedProjectFormatException` — for reading and writing alike,
  since a newer layout may keep things where an older store would never look or would write over
  them. Raise it whenever an on-disk change would make an older store misread a project. It
  matters because the two CLIs release independently and `sqetch-deploy` reads projects that
  whichever `sqetch` each maintainer has wrote.
- **Journaling statements are project-level and not frozen**: they live in `journaling/` at the
  project root, written atomically like every other store file, and — unlike step scripts — have no
  finalized-release guard, so they can change after a release is finalized while an already-built
  bundle keeps the SQL it was built with. `ProjectPaths` owns the paths and
  `JournalingSlots.FileName` the names, so the store and the bundle layout cannot drift apart.
- **`plan-index` is derived data**: plan.json files and folder locations are the source of
  truth; the index is rebuildable (`sqetch project reindex`).
- **Pluggable families** (sharding strategies, bundle types, bundle layouts, database
  drivers): contracts in `Pug.Sqetch.Abstractions` (in the family's folder), implementations
  in sibling projects, a registry keyed by name (`OrdinalIgnoreCase`). Bundling and
  deployment registries ship **empty**; the host registers built-ins via the `Register*()`
  extension methods (see `SqetchDeployApp.CreateDefaultRegistrar`).
  Journaling is deliberately *not* a family:
  the SQL is the project's, so there is nothing to select at deploy time and no
  `--driver`/`--journal` pair to mismatch.
- **Bundles are read through the same families**: `IBundleType.Open` returns an
  `IBundleReader` (archives extract to a temp dir deleted on dispose); `IBundleLayout`
  translates its on-disk manifest schema to/from the layout-neutral `BundleManifest`
  (`ReadManifest`) and owns entry pathing (`ScriptPath`). Manifest array order IS the
  deployment order.
- **Bundling belongs to `sqetch-deploy`**, the tool that reads bundles, so the writer and the
  reader of the format are one program. `bundle --project <dir>` (default: the current
  directory) opens the project **read-only** (`ProjectFactory.CreateReadOnly`, no `UserInfo`), so
  a pipeline needs no `.sqetch-user`; a read-only `Project` refuses any write reached by casting it
  back to `IProject`. The bundle stays a build-once artifact: deploying never builds one.
- **Every bundle starts with `META-DATA`** (`BundleMetadata`), apart from the manifest so a
  reader learns whether it can read the bundle before reading anything a layout owns: UTF-8
  `key=value` lines (`#` comments and unknown keys ignored, known keys exactly once) carrying
  `format`, `layout`, `bundler`, `bundler-version` and `bundled` (UTC, `yyyy-MM-ddTHH:mm:ssZ`).
  Its name, place and syntax belong to no layout and must never change; `BundlePackager` writes it
  before the layout writes anything, so no layout can forget it. `BundleValidator` reads it first,
  resolves the layout by the name it gives, and refuses an unknown layout or a `format` above
  `IBundleLayout.Format` (`UnsupportedBundleFormatException`). The compatibility gate is layout
  plus format, **never the bundler version**, which is there for pinning and printed by `deploy`.
- **Bundles carry the release lineage**: every manifest release records its `dependency` (the
  release it follows; empty starts the lineage), and a bundle's releases must form a
  contiguous chain — a manifest omitting `dependency` normalizes it to the preceding release,
  since array order is already the contract. `bundle --since <release>` emits a *continuation
  bundle* holding only the releases after the named one.
- **Journaling is project-authored SQL** (`JournalingSlot`, `JournalingStatements` in
  `Pug.Sqetch.Models`): fifteen slots — twelve deployment events, the two queries
  `GetLatestRelease`/`GetDeployedPlans`, and `PrepareJournal` — set per project via
  `IProject.SetJournalingStatement`, stored as `journaling/<slot>.sql`, and carried in the bundle.
  A slot may hold several statements, separated by a line whose trimmed content is exactly `;;`;
  that is the **only** parsing Sqetch does to the SQL, so a `$$ … $$` body containing a bare `;;`
  alone on a line would split wrongly. Query slots hold exactly one statement. Parameters are
  supplied by name without a provider prefix (`@project` on every slot, plus `@release`, `@plan`,
  `@step`, `@description` as the slot carries them, and `@utcTimestamp` last on every *writing* slot
  — see `JournalingSlots.ParameterNames`/`Writes`); binding is the driver's job and Sqetch never
  scans SQL for placeholders, so a statement that names none of them is fine. `@utcTimestamp` is the
  deployment host's clock, bound as a `DateTime` with `DateTimeKind.Utc` rather than text (no
  provider implicitly casts text to a timestamp column) and read **once per journaled event**, not
  per statement. It exists for the engine that cannot supply the instant itself; a maintainer whose
  engine can is better served by `now()`, which no skewed host clock can disorder. Deliberately
  absent: an actor (`current_user`). Queries and `PrepareJournal` get no timestamp — one filters on
  identity, the other is DDL that records no event. `bundle` refuses until all fifteen are set, naming every missing one;
  rollback slots are required even though rollback deployment is not implemented.
  CLI: `sqetch journaling list [--statements] [-o <FORMAT>]`, `parameters [<slot>] [-o <FORMAT>]`
  (answers from the contract alone, so it needs no project), `print <slot>` (verbatim, no markup,
  so `> file.sql` round-trips) and `set <slot>` taking exactly one of an SQL argument, `--file` or
  `--stdin`. SQL beginning with `--` cannot be passed as the argument — a leading-dash positional
  parses as an option — so a comment-first statement needs `--file` or `--stdin`.
- **Query result contracts** (`StatementJournal`): read by **ordinal**, first N columns are the
  contract and extras are ignored — Oracle folds unquoted identifiers, and `GetOrdinal`'s case
  sensitivity is provider-defined. `GetLatestRelease` returns zero or one row (column 0 release
  name, column 1 completed); more than one row throws, and a NULL release name throws rather than
  being coerced, because `""` already means the pseudo-release. `completed` coerces from bool,
  any numeric (`!= 0`), or `1/y/yes/t/true` case-insensitively. Failures are
  `JournalingStatementException` naming the slot and the 1-based statement within it.
- **Deploy semantics** (`DeploymentEngine`): the journal alone decides where deployment starts,
  in four branches — nothing journaled deploys the bundle whole; an **incompletely** deployed
  release must be *contained* in the bundle and resumes inside it; a **completely** deployed
  release hands over to whichever group *depends* on it, which may be a release the bundle does
  not contain (continuation bundle). `deployedPlans` resumes inside the journaled group,
  skipping the plans it **names** and deploying the rest in bundle order (and journaling that
  release's completion when none are left, without journaling its start again); the journaled
  group alone starts at the group depending on it; neither starts at the first group — refusing
  when the first group has a dependency. `GetLatestRelease` returns `JournaledRelease?`
  (`null` = fresh database, `Name == ""` = the unreleased-plans pseudo-release, `Completed` = its
  `ReleaseDeployed` was journaled) and `GetDeployedPlans` is an **unordered set** — resume is by
  membership, never by position, because a release's plans are only partly ordered (disjoint
  dependency chains can be interleaved either way, and the journal records nothing that pins
  which interleaving a past run used). A journaled plan the bundle's copy of the release lacks is
  `IncompatibleBundleException`. The pseudo-release depends on the bundle's last real release,
  so it is an ordinary link in the chain; nothing may depend on *it*, so a database left at the
  pseudo-release is refused rather than matched to a lineage-starting bundle. A completed
  release is never resumed into, so plans a bundle gained for one are not deployed — test
  databases are single-use. Re-deploying an unchanged bundle is `NothingToDeploy`, not an error.
  Mismatches are `IncompatibleBundleException` (the bundle is well formed but does not fit the
  database's state); `InvalidBundleException` stays for malformed manifests.
- **Transaction order** (`DeploymentEngine`): every deployment opens `PrepareJournal` in a
  transaction committed **alone** (the journal's schema must exist before it can be read, and DDL
  auto-commits on some engines), then **both** journal queries in one transaction (so the resume
  decision comes from one consistent view), then the deployment itself — committed at plan or
  release boundaries per `DeploymentCommitLevel`, with `ReleaseDeployed` always journaled in the
  same transaction as the release's last plan. Neither the prepare nor the read commit is reported
  to `IDeploymentListener`: they are not deployment progress. On failure the open transaction rolls
  back — earlier commits stand.
- **Rollback is compensating, not transactional** (`RollbackTrigger`, `deploy --rollback`):
  `on-error` undoes what the run committed after a failure, `on-success` undoes a deployment that
  worked — for proving a bundle applies cleanly, usually a test bundle, though a bundle of
  finalized releases is not refused: what a caller wants to prove and then undo is its own
  business. It runs each step's `rollback.sql` in reverse (releases, then plans, then steps) and
  journals through the `RollingBack*`/`RolledBack*` slots. It is **independent of
  `DeploymentCommitLevel`**, which is the whole point: at plan level the earlier plans are already
  durable and no database transaction could take them back. The engine therefore records committed
  units — a plan moves from pending to committed only when its transaction commits, so a plan the
  open transaction took back is never compensated for a change that never reached the database. A
  failing rollback script throws `RollbackFailedException` and replaces the failure that triggered
  it: a half-compensated database is worse than either end state, and the reason was already
  reported to the listener. The standalone `sqetch-deploy rollback` command is still a stub;
  rollback today is a mode of `deploy`.
- **Database drivers are ADO.NET wrappers, not SQL translators**: `AdoDatabaseDriver` holds the
  connection, the transactions on it and the command plumbing; a provider project supplies only a
  connection factory and a factory implementing `IDatabaseDriverFactory`. Journaling parameters are
  bound **by their bare name, all of them**, including the ones a statement never mentions —
  verified per provider, since neither behaviour can be read off a provider's documentation
  (Npgsql ignores an unreferenced parameter despite rewriting named placeholders to positional
  ones, and both providers resolve a prefixless name against every placeholder form they accept).
  A provider needing the prefix spelled out, or refusing what it cannot see, belongs in
  `AdoDatabaseTransaction.Bind` *with a test proving the need* — an earlier dialect-plus-SQL-scan
  for exactly that was deleted once the probe showed no provider needed it.
  **One connection per driver**, opened on first use and reused: a deployment's transactions are
  strictly sequential, and session state a maintainer's statements set up survives between them. A
  second overlapping transaction is refused as a bug. `IDatabaseDriver` is `IDisposable` because it
  owns that connection, and disposing it **rolls back** an open transaction rather than committing
  it; `DeployCommand` owns the lifetime. The step script timeout applies to step scripts alone —
  a journaling statement waiting on a lock is left to the provider's default. A query's `DbCommand`
  outlives its reader (disposing the command closes the reader), so both are released when the
  journal disposes the reader or the transaction ends.
- **`sqetch-deploy drivers` answers from the registry alone** (`drivers list`, `drivers parameters
  [<driver>]`), so unlike every other command it needs neither a bundle nor a database — which is
  the point: whoever is reaching for a driver name has neither to hand yet. `parameters` prints the
  **required sets first and the per-parameter descriptions after**, because required-ness is a fact
  about *sets* (`[["host","database"],["connection-string"]]`) and nothing per-parameter can express
  it — a column marking each one required or not would turn a choice between two ways of connecting
  into seven independent flags. Switches are shown as a caller types them (`--postgres-host`), the
  way `journaling parameters` shows `@project`. A driver with no required sets still gets an empty
  sets section, so the result's shape does not depend on what it finds. Because `StrictParsing` is
  off app-wide, both commands must call `RemainingArguments.RejectAll` — without it a typo'd option
  lands in `IRemainingArguments` and the command reports success. Listing every driver constructs
  every factory, since `GetParametersDefinition` is an instance method; harmless while factories are
  connectionless, but a factory doing real work in its constructor would do it merely to be listed.
  A bare `sqetch-deploy drivers` prints the branch help and exits `1`, which is Spectre's own
  behaviour for a branch without a command (`sqetch journaling` does the same) rather than a code
  this CLI chose.
- **`sqlite` driver**: `--sqlite-file <PATH>` (created if missing) or `--sqlite-connection-string`,
  mutually exclusive. Pass `Mode=ReadWrite` in a connection string to refuse a missing file, which
  is how a mistyped path is caught.
- **`postgres` driver**: `--postgres-host` and `--postgres-database` (plus `port`, `username`,
  `password`, `password-env`), or `--postgres-connection-string`, which *replaces* them rather than
  extending them. `password-env` names an environment variable holding the password, since a command
  line is readable by every process on the host. Journaled instants arrive as `timestamptz`; a
  `timestamp` column would store whatever the session time zone made of them.
- **Both engines run DDL inside a transaction**, so a release deployed at release commit level
  really is all-or-nothing on them — the engines that commit DDL implicitly are what make the
  compensating rollback necessary, not these.
- **Driver tests run against real databases**: SQLite in temp files (never `:memory:`, whose
  contents die with the connection), PostgreSQL in a Testcontainers container gated by
  `[DockerFact]`/`[DockerTheory]`, which *skip* where the daemon socket cannot be reached rather
  than failing. `RealDatabaseDeployment` holds the end-to-end scenarios so both providers run the
  same ones — the only place the engine's resume decision is read back out of SQL the deployment
  itself wrote, and the only check that a journal's `completed` column works as both a count
  (SQLite) and a boolean (PostgreSQL). Compensating rollback is among them: that a committed plan is
  undone by its own `rollback.sql`, that `on-success` undoes releases and their plans in reverse,
  and that a failing rollback script leaves no half-finished compensation behind — the last of which
  only a real server can show, since it turns on the compensation transaction rolling its journal
  rows back with it.
- **Both CLIs present through one writer**: `Pug.Sqetch.Cli.Common` owns the `-o|--output` contract
  (`OutputSettings`, `OutputFormat`) and `ResultWriter` — JSON, CSV, a Spectre table on an
  interactive console and header-less tab-separated rows when piped. Its types are public because
  other assemblies consume them. The CSV quoting, the JSON options and the interactive-vs-piped
  decision are exactly what drifts when duplicated. Both hosts reference `Pug.Sqetch.Cli.Commands`,
  which holds every command of both CLIs, so **'sqetch-deploy' ships the authoring assemblies too**
  (engine, file-system store, bundle builder) — which `bundle` now needs anyway; the
  hosts stay thin, and only `Pug.Sqetch.Deployment.Cli` references the concrete database drivers. **The machine formats
  are written with wrapping widened out of the way**: Spectre wraps to the profile width, which is
  80 whenever stdout is not a terminal — precisely when those formats are used — and a wrapped line
  is a corrupt record, not an ugly one: the newline lands inside a tab-separated field, a quoted CSV
  cell or a JSON string, where it is an invalid control character. `WriteSections` renders a result
  made of several tables (a set of groupings, then the things grouped): one JSON document, one CSV
  block per section keyed by the section's name, and piped rows prefixed with that key, since
  header-less rows from two sections are otherwise indistinguishable. `Pug.Sqetch.Tests.Cli.Common`
  pins all of it against a deliberately narrow, non-interactive console.
- **`sqetch-deploy` exit codes are categorized** (`DeployExitCodes`, public because they are the
  contract a pipeline reads): the tens digit names the category and the units digit the failure
  within it, so a caller can branch on a decade without knowing every member. A units digit of `0`
  is the category's *unspecified* code, reserved so adding a specific code never renumbers an
  existing one; a *one-digit* code means unclassified, and `1` is still what an unrecognized
  exception returns, so a `!= 0` test is unaffected. `0` covers deployed, nothing-to-deploy and
  deployed-then-undone alike — anything non-zero stops a `set -e` shell, and telling the three
  apart is the output's job. `10` is every command-line mistake, deliberately undivided: finer
  codes would mean parsing Spectre's message text or re-implementing its parser, and
  `CommandParseException` carries no reason of its own. Then `21`/`22`/`23`/`24` missing / invalid /
  incompatible / unsupported bundle (`24`: an unknown layout or a newer layout format — the remedy
  is upgrading the tool, not fixing the bundle), `31`/`32`/`33` unknown driver / driver creation / database unreachable, `41`/`42`/`43` journal
  prepare / query / write, `51`/`52` deployment failed with the database unchanged / left partway,
  `61` rollback failed, `71`/`72`/`73` for `bundle`: project unreadable (missing, damaged, newer
  format) / not bundleable (unset journaling, missing scripts, a `--since` with nothing after it,
  an unknown release) / output not writable (`BundleOutputException`, wrapped by the command so
  no `IOException` elsewhere lands on it), `91` not implemented. Two precedence rules carry the operational meaning:
  a half-compensated database (`61`) outranks the failure that started the rollback, and a database
  left partway (`52`) outranks the cause of it — the cause stays in the message for whoever fixes
  it, the code tells a pipeline whether it may retry. `52` therefore needs to know whether anything
  committed, which only `DeployCommand` does, so **it returns its own code** and the central
  `SetExceptionHandler` sees only what was raised before the deployment began. `UnknownBundleType`
  is `10`, not `22` — the only bundle type name is the one `--bundle-type` carries; an unknown
  layout named by a bundle is `24`, and one asked for by `bundle` is `1`, since that name is the
  host's own constant. `33` exists because a driver is
  configured without connecting: an unreachable server is not a creation failure and does not
  surface until the first transaction, so `AdoDatabaseDriver` turns a failed open into
  `DatabaseConnectionException` rather than letting a provider exception reach the handler as `1`.
- **Names** (`NameRules` in the FileSystem store, surfaced early via CLI `NameValidation`):
  letters, digits and `-_+()@#.`; must start with a letter or digit, must not end with `.`,
  max 128 chars. Names become path segments and git paths.
- **`GetStepScripts` ownership**: the engine returns open streams the caller must dispose,
  and throws `MissingStepScriptsException` when a script file is missing;
  `VerifyStepScripts( plan )` checks a whole plan.

- **Releases are tag-driven and all logic is PowerShell** (`RELEASING.md`): `.github/workflows/release.yml`
  holds only triggers, runners, permissions and artifact plumbing, and every job calls a
  `build.ps1 -Stage` that runs the same on a workstation. `build.ps1` dot-sources `Common.ps1` and
  exactly one platform file, each of which defines `Invoke-PlatformSigning`, `New-PlatformPackages`
  and `Test-Installers`, so no stage branches on the platform. Smoke checks are written once
  (`Test-ProductCommands`) against a *product invoker*, so the same checks run a binary directly,
  from PATH after an MSI install, or through `docker exec` inside the container a .deb/.rpm was
  installed into. `sqetch-deploy`'s smoke deploys the committed `Installers/smoke/bundle` to SQLite,
  which is what proves the native `e_sqlite3` beside the single file loads, then bundles the
  committed `Installers/smoke/project` (mounted read-only in the containers, and without a
  `.sqetch-user`) and deploys that too; regenerate both with `New-SmokeBundle.ps1` whenever the
  project format, the bundle format or the journaling contract changes. The two CLIs release
  independently (`sqetch-v*`, `sqetch_deploy-v*`); the tag's `X.Y.Z` must equal the host project's
  `<Version>`. A `-pre-` tag builds `X.Y.Z-pre.<UTC stamp>` unsigned; a plain tag is signed when all
  the signing variables are set, unsigned (with a warning, and a note in the draft) when none is, and
  refused when only some are.
  Both push the dotnet tool package to nuget.org (`push-tool`, NuGet trusted publishing, so no stored
  key) once every smoke job passed, and only then create the draft GitHub release.
  Formats that cannot carry the suffix keep `X.Y.Z` (MSI ProductVersion) or translate it (deb/rpm
  `~pre…`, which sorts before the release), but every **file name** carries the full version as
  written, because GitHub rewrites `~` in asset names and `SHA256SUMS` must match them.

## Conventions

- Tabs for indentation; spaces inside parentheses: `Foo( bar, baz )`; file-scoped namespaces.
- **Namespaces follow the project name and folder**: a production project's namespace is its name
  with any `.Common` or `.Abstractions` suffix dropped, plus the folder path — so
  `Pug.Sqetch.Cli.Common` is `Pug.Sqetch.Cli`, and `Pug.Sqetch.Abstractions`' `DatabaseDriver/`
  folder is `Pug.Sqetch.DatabaseDriver`. Each
  project's `RootNamespace` encodes the rule, so Rider's *Adjust Namespaces* keeps files in line. The
  sqetch CLI imports its `Rows` helpers through an alias because `Spectre.Console.Rows` shares the name.
- Exceptions: plain classes with get-only properties and an optional message via `base(...)`
  (see `UnknownPlanException`); mapped to friendly CLI messages in `CliErrors.Describe`,
  surfaced by the central `SetExceptionHandler` in `SqetchApp` with exit code 1.
- Tests: xunit. sqetch CLI tests drive the real command tree through `CommandAppTester` and
  switch the process-wide current directory — every test class that does so must join
  `[Collection( "cli" )]` to stay sequential. sqetch-deploy CLI tests instead pass absolute
  paths and inject fakes via the production `TypeRegistrar` (`CommandAppTester( registrar )`),
  so they need no collection — except the one test of `bundle`'s default output name, which must
  switch the current directory and runs alone in the `"cwd"` collection (`DisableParallelization`).
  `bundle` tests build their projects through the engine (`BundleTestProjects`), not the 'sqetch'
  CLI. Store/engine tests build on `TempProject`,
  `TestData` and `ShardingCases` from `Pug.Sqetch.Tests.Stores.FileSystem/TestSupport.cs`;
  git legibility is asserted with real `git` via the `Git` helper.
- Verify changes with the full `dotnet test` plus, for user-visible behavior, a live CLI run
  in a temp directory (`project user`, `project init`, then the flow under test).
