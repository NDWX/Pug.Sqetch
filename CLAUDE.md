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
dotnet run --project src/Cli/Planning/Pug.Sqetch.Planning.Cli -- <args>   # run the 'sqetch' CLI
dotnet run --project src/Cli/Deployment/Pug.Sqetch.Deployment.Cli -- <args>   # run the 'sqetch-deploy' CLI
dotnet run --project src/Cli/Deployment/Pug.Sqetch.Deployment.Cli -- bundle --project <dir> -o <bundle>
dotnet run --project src/Cli/Deployment/Pug.Sqetch.Deployment.Cli -- deploy <bundle> --driver sqlite --sqlite-file db
dotnet run --project src/Cli/Deployment/Pug.Sqetch.Deployment.Cli -- status <bundle> --driver sqlite --sqlite-file db   # what deploy would do
dotnet run --project src/Cli/Deployment/Pug.Sqetch.Deployment.Cli -- drivers parameters sqlite   # a driver's switches
pwsh Installers/build/build.ps1 -Stage <stage> ...   # release build; see RELEASING.md
```

## Solution layout

The solution has two domains, each a family of projects named after it: **Planning** (authoring
projects, the 'sqetch' CLI) and **Deployment** (bundling and deploying them, the 'sqetch-deploy' CLI).
The database drivers and the CLI presentation layer sit beside them.

Every project lives under `src/`, in the folder matching its solution folder: the four contract and
model projects and `Pug.Sqetch.Shared` (with its tests) at `src/` itself, then `src/Planning`, `src/ProjectInfoStores`, `src/Deployment`,
`src/DatabaseDrivers`, `src/Cli` (the shared CLI layer), `src/Cli/Planning` (the 'sqetch' commands
and host), `src/Cli/Deployment` (the 'sqetch-deploy' commands, host and bundling contracts) and
`src/Cli/Deployment/Bundling`. Each test project sits beside what it tests. A new project goes in
the folder of its solution folder, and the two must stay the same. `Installers/`, `actions/`,
`claude/` and the repository files (`Directory.Build.props`, `Directory.Build.targets`, `global.json`, licences) stay at the
root.

| Project | Role |
|---|---|
| **Planning** | |
| `Pug.Sqetch.Planning.Models` (net8.0) | Plain records: definitions, criteria, `StepScripts`, `UserInfo`, the journaling slots and statements |
| `Pug.Sqetch.Planning.Abstractions` (net8.0) | The authoring contracts: `IProject`/`IReadOnlyProject`, `IScriptsStore`, `IReleaseDependencyDeterminator`, domain exceptions; `ProjectInfoStore/` — `IProjectInfoStore` |
| `Pug.Sqetch.Planning` | The engine (`Project`): business rules, dependency ordering, semaphores |
| `Pug.Sqetch.Planning.ProjectInfoStores.FileSystem` | Git-friendly file store: `ProjectPaths`, sharding, plan index, `NameRules` |
| `Pug.Sqetch.Cli.Commands` (net10.0) | The 'sqetch' commands (project, plan, step, release, journaling), `OptionParsing`, `Output/Rows`. References the driver contract, not the drivers |
| `Pug.Sqetch.Planning.Cli` | The 'sqetch' host: `Program`, the command tree (`SqetchApp.Configure`), the default composition (`SqetchApp.CreateDefaultRegistrar`: the drivers, for their journaling templates, the template sources and the warning writer) and `CliErrors`. Its tool package keeps the id `Pug.Sqetch.Cli` |
| **Deployment** | |
| `Pug.Sqetch.Deployment.Bundling.Abstractions` (net8.0) | The bundling contracts: `Bundle`/`BundleManifest` models, `BundleMetadata` (the `META-DATA` entry), `IBundleType`/`IBundleWriter`/`IBundleReader`/`IBundleLayout`, registry interfaces, bundling exceptions. References only `Planning.Models` |
| `Pug.Sqetch.Deployment.Bundling.BundleTypes` | zip / tar / tar.gz / directory implementations + `RegisterBundleTypes()`; references only the bundling contracts |
| `Pug.Sqetch.Deployment.Bundling.Layouts` | `DefaultBundleLayout` + `RegisterLayouts()`; references only the bundling contracts |
| `Pug.Sqetch.Deployment.Bundling` | `BundleBuilder` (assembles from an `IReadOnlyProject`) and `BundlePackager` (writes `META-DATA`, then the layout) + registry implementations; the one bundling project that also references the planning contracts |
| `Pug.Sqetch.Deployment.Abstractions` (net8.0) | The deployment contracts: what the engine deploys (`ReleaseGroup`/`DeployablePlan`/`DeployableStep`), where it gets the SQL (`IScriptContentReader`: each step's deploy, verify and rollback script), `JournaledRelease` and the deployment exceptions (`DeploymentException` and its subclasses, `StepVerificationFailedException` deriving from `StepScriptFailedException`). References nothing |
| `Pug.Sqetch.Deployment` (net8.0) | `DeploymentEngine` (deploy flow), `DeploymentPlanner` (the pure resume decision, `DeploymentForecast`), `DeploymentForecaster` (the read-only forecast `status` prints), `StatementJournal`, `IDeploymentListener`. References the deployment and driver contracts and `Planning.Models` (for the journaling statements) — **neither bundling nor the planning contracts**: the engine deploys release groups it is handed and reads scripts through `IScriptContentReader`, so a caller can deploy from any source |
| `Pug.Sqetch.Deployment.Cli.Commands` (net10.0) | The 'sqetch-deploy' commands (`bundle`, `deploy`, `status`, `rollback`, `drivers`) and their helpers: `DeploymentTarget` (bundle type and driver creation, shared by `deploy` and `status`), `BundleValidator`/`ValidatedBundle`, `BundleReleaseGroups` (manifest → release groups), `BundleScriptContentReader` (the bundle-backed `IScriptContentReader`), `BundlerIdentity`, `DeploymentSettings`, `DriverParameters`, `RemainingArguments`, `ConsoleDeploymentListener`, `DeployCliErrors`, `DeployExitCodes`. Everything that ties deployment to bundles lives here |
| `Pug.Sqetch.Deployment.Cli` | The 'sqetch-deploy' host: `Program`, the command tree and default composition (`SqetchDeployApp`, one of the two hosts referencing the concrete drivers) |
| **Database drivers** | |
| `Pug.Sqetch.DatabaseDriver.Abstractions` (net8.0) | The driver contract: `IDatabaseDriverFactory`/`IDatabaseDriver`/`IDatabaseTransaction`, `JournalingParameter`, the parameter definitions, registry interface, `IDatabaseDriver.CommitsDdlImplicitly` (a default member, false), the optional `IJournalingTemplateProvider` (a factory's built-in journaling template, keyed by slot name), driver exceptions (based on `DatabaseDriverException`, not `DeploymentException`). References nothing, so a driver depends on neither domain |
| `Pug.Sqetch.DatabaseDrivers.Ado` (net8.0) | `AdoDatabaseDriver`/`AdoDatabaseTransaction`: the ADO.NET half every provider driver inherits, with `AdoDriverOptions` (a provider's step-script splitter and parameter adjustment) and `StepScriptBatchFailedException`; `ServerConnectionParameters`, the host/port/database/username/password(-env)/connection-string rules every server driver shares; and `EmbeddedJournalingTemplate`, which reads a driver's `journaling/<slot>.sql` resources; references only the driver contract |
| `Pug.Sqetch.DatabaseDrivers.Sqlite` | `SqliteDatabaseDriverFactory` (Microsoft.Data.Sqlite) + `RegisterSqliteDriver()`; `Journaling/*.sql`, its built-in journaling template |
| `Pug.Sqetch.DatabaseDrivers.PostgreSql` | `PostgreSqlDatabaseDriverFactory` (Npgsql) + `RegisterPostgreSqlDriver()`; `Journaling/*.sql`, its built-in journaling template |
| `Pug.Sqetch.DatabaseDrivers.SqlServer` | `SqlServerDatabaseDriverFactory` (Microsoft.Data.SqlClient, managed networking) + `RegisterSqlServerDriver()`, `SqlServerBatches` ('GO' splitting); `Journaling/*.sql`, its built-in T-SQL template |
| `Pug.Sqetch.DatabaseDrivers.MySql` | `MySqlDatabaseDriverFactory` (MySqlConnector; MySQL and MariaDB) + `RegisterMySqlDriver()`; `Journaling/*.sql`, its built-in template |
| **Shared** | |
| `Pug.Sqetch.Shared` (net8.0) | Implementations both CLIs' back ends use, so neither depends on the other's: `DatabaseDriverRegistry`. References only the driver contract |
| **Shared CLI** | |
| `Pug.Sqetch.Cli.Common` (net10.0) | What both CLIs present and open projects with: `OutputFormat`/`OutputFormats`, `OutputSettings` (`-o|--output`), `ResultWriter`; `ProjectSession` (also opened read-only by `bundle`), `NameValidation`, `UserIdentity`, `Defaults`; `TypeRegistrar` (both hosts' DI seam), `WarningWriter` (warnings on standard error), `JournalingTemplateSources`. The only layer touching `ProjectPaths` for output |
| **Tests** | xunit, one project per tier: `Pug.Sqetch.Planning.Tests` (engine), `Pug.Sqetch.Tests.Planning.Stores.FileSystem`, `Pug.Sqetch.Tests.Planning.Cli`, `Pug.Sqetch.Tests.Deployment.Bundling`, `Pug.Sqetch.Tests.Deployment`, `Pug.Sqetch.Tests.DatabaseDrivers`, `Pug.Sqetch.Tests.Deployment.Cli`, `Pug.Sqetch.Tests.Cli.Common`, `Pug.Sqetch.Tests.Shared` |
| `Installers/` | Release packaging (GPL): `Publish.props` (self-contained single-file when a RID is set, imported by both hosts), `build/` (`build.ps1` + `Common.ps1` + `Image.ps1` + one of `Windows.ps1`/`Linux.ps1`/`MacOS.ps1`), `Wix/` (MSIs), `nfpm/` (deb/rpm), `chocolatey/`, `docker/` (the `sqetch-deploy` image), `smoke/` (project and bundle fixtures + `New-SmokeBundle.ps1`) |
| `actions/` | GitHub Actions for users (GPL): `install-sqetch-deploy` (composite; `install.sh` in bash, since some arm64 runner images lack pwsh) |
| `claude/pug-sqetch/` | The agent skills plugin for Sqetch's users (MIT): `sqetch` (entry point, concepts, versions, help), `sqetch-install`, `sqetch-new-project` (with tested journaling templates), `sqetch-plans-and-releases`, `sqetch-bundle-and-deploy`, `sqetch-ci`. Listed in the `pug-claude-plugins` marketplace (`NDWX/claude-code-plugins`) from this repository's `main` |

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
  drivers): contracts in the area's `*.Abstractions` project (`Pug.Sqetch.Deployment.Bundling.Abstractions`,
  `Pug.Sqetch.DatabaseDriver.Abstractions`; sharding lives inside the file-system store), implementations
  in sibling projects that reference only those contracts, a registry keyed by name (`OrdinalIgnoreCase`). Bundling and
  deployment registries ship **empty**; the host registers built-ins via the `Register*()`
  extension methods (see `SqetchDeployApp.CreateDefaultRegistrar` and `SqetchApp.CreateDefaultRegistrar`).
  Journaling is deliberately *not* a family:
  the SQL is the project's, so there is nothing to select at deploy time and no
  `--driver`/`--journal` pair to mismatch.
- **Bundles are read through the same families**: `IBundleType.Open` returns an
  `IBundleReader` (archives extract to a temp dir deleted on dispose); `IBundleLayout`
  translates its on-disk manifest schema to/from the layout-neutral `BundleManifest`
  (`ReadManifest`) and owns entry pathing (`ScriptPath`). Manifest array order IS the
  deployment order.
- **The deployment engine knows no bundles**: `DeploymentEngine.Deploy( project, groups )` takes
  `ReleaseGroup`s in lineage order and reads each step's SQL through an `IScriptContentReader`
  (given the release, plan and step names), so a developer can deploy from a source of their own.
  Determining the groups is the caller's job; the engine only checks the lineage it relies on —
  unique names, each group depending on the one before — and refuses a broken one with
  `ArgumentException` before touching the database. For bundles, the CLI does both halves:
  `BundleValidator` groups the manifest through `BundleReleaseGroups.From` (a malformed one is
  `InvalidBundleException`, exit code `22`, still before any driver exists) and `deploy` hands the
  engine a `BundleScriptContentReader` over the open bundle and its layout.
- **Bundling belongs to `sqetch-deploy`**, the tool that reads bundles, so the writer and the
  reader of the format are one program — and it is a CLI concern: no deployment library references
  the bundling projects. `bundle --project <dir>` (default: the current
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
  `Pug.Sqetch.Planning.Models`): fifteen required slots — twelve deployment events, the two queries
  `GetLatestRelease`/`GetDeployedPlans`, and `PrepareJournal` — and the **optional** query
  `JournalExists` (`JournalingSlots.IsOptional`/`Required`), set per project via
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
  identity, the other is DDL that records no event. `bundle` refuses until all fifteen required slots
  are set, naming every missing one; rollback slots are required even though rollback deployment is
  not implemented. **An optional slot** is one bundling and deploying never need, so projects,
  templates and bundles written before it existed stay complete: `JournalingStatements.Missing`
  ignores it, `IsSet` says whether it is there, `Text`/`Statements` throw for it unset, the layout
  writes it only when set and reads its absence as unset, and older tools never look for its file,
  so adding one needs no format bump. A command needing one refuses statements lacking it with
  `JournalingStatementsNotSetException` (general: slots and purpose), exit `25`, before connecting —
  it never guesses without it. `JournalExists` answers whether the journal exists yet (one row,
  column 0 coerced like `completed`; no row is false), from the catalog rather than the table, so
  `status` can tell a database never deployed to from a failing query without running
  `PrepareJournal`.
  CLI: `sqetch journaling list [--statements] [-o <FORMAT>]` (columns are only ever appended: `Optional`
  follows `Set` without `--statements`, and is left out of the tabular formats with it, since the
  multi-line `Statement` must stay last; JSON always carries it), `parameters [<slot>] [-o <FORMAT>]`
  (answers from the contract alone, so it needs no project), `print <slot>` (verbatim, no markup,
  so `> file.sql` round-trips) and `set <slot>` taking exactly one of an SQL argument, `--file` or
  `--stdin`. SQL beginning with `--` cannot be passed as the argument — a leading-dash positional
  parses as an option — so a comment-first statement needs `--file` or `--stdin`.
- **Journaling templates** (`journaling init [<driver>] [--template <name>] [--force]`,
  `journaling templates [<driver>]`): a template is a starting point copied into the project, after
  which the project owns the SQL — journaling is still not chosen at deploy time. Each driver ships
  its `default` template (`IJournalingTemplateProvider`, embedded `Journaling/<slot>.sql`), because
  the SQL is its engine's dialect and must suit its binding; the real-database scenarios run on
  exactly those templates, and a test keeps the skills' reference copies identical to them.
  `JournalingTemplateSources` looks, in order, at the repository level (the nearest
  `.config/sqetch/` from the project folder up to the git work-tree root; only the project folder
  outside git), the user level (`ApplicationData/sqetch`: `~/.config/sqetch`, `%APPDATA%\sqetch`,
  `~/Library/Application Support/sqetch`) and the driver. Below either root a template is
  `database-drivers/<driver>/journaling-templates/<name>/` with one file per required slot (and per
  optional slot it sets) named by `JournalingSlots.FileName`, so a project's `journaling/` folder
  copies in as it is, and templates written before an optional slot existed stay valid; the built-in
  ones set every slot. Repository
  wins over user — the team's choice for that codebase over one person's — and a template is used
  whole, never mixed across levels, since two templates' SQL can disagree about tables. `init`
  refuses when any slot is set unless `--force`; the store is not transactional, so a failure
  partway says to rerun with `--force`. Without a driver argument it uses the project's engine when
  that names a registered driver; `project init --engine` stays free text but warns when it names
  none, and `init` warns when an explicit driver differs from the engine. Warnings go to standard
  error through `WarningWriter`, keeping standard output clean for scripts.
- **Query result contracts** (`StatementJournal`): read by **ordinal**, first N columns are the
  contract and extras are ignored — Oracle folds unquoted identifiers, and `GetOrdinal`'s case
  sensitivity is provider-defined. `GetLatestRelease` returns zero or one row (column 0 release
  name, column 1 completed); more than one row throws, and a NULL release name throws rather than
  being coerced, because `""` already means the pseudo-release. `completed` coerces from bool,
  any numeric (`!= 0`), or `1/y/yes/t/true` case-insensitively. Failures are
  `JournalingStatementException` naming the slot and the 1-based statement within it.
- **Forecasting** (`sqetch-deploy status`, `DeploymentForecaster`): the resume decision is
  `DeploymentPlanner.Plan( groups, latest, deployedPlans )` — pure, returning a `DeploymentForecast`
  (the notes — releases skipped, the release continued from — in order, and the `PlannedRelease`s
  with their skipped plans and whether they resume) — which the engine replays to its listener and
  deploys, and which the forecaster reports, so the decision itself is one implementation. What they
  read can still differ: `status` skips `PrepareJournal`, so a `PrepareJournal` that migrates the
  journal, or a `JournalExists` looking somewhere the queries do not, can make them disagree.
  `EnsureLineage` and `EnsureResumable` (a partly deployed release the groups lack, refused before
  its plans are queried) are the planner's too. The forecaster refuses statements without
  `JournalExists` before opening a transaction, asks it, and on "no" plans a fresh database without
  another query; otherwise it runs both journal queries. Everything runs in one transaction that is
  **always rolled back**, and a rollback failing after a failure never replaces it. On SQLite the
  provider begins with `BEGIN IMMEDIATE`, so even this read holds the write lock briefly.
  `status` prints the state (`fresh`/`empty`/`partial`/`complete`), the releases that would deploy
  (resumed or not, plans left — a release with none left still journals its completion, which is
  why it is listed apart from the plans) and the plans through `WriteSections` (`database`,
  `releases`, `plans`), leaves failures to the
  central handler (it can have changed nothing), and exits `0` — or, with `--exit-code`, `81` when
  something would deploy, the 80s being "status outcomes", not failures. It checks `JournalExists`
  before creating a driver, so a refused bundle never connects.
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
  database's state); `InvalidBundleException` stays for malformed manifests, which the CLI's
  grouping now raises, not the engine. "Bundle" here reads as "the groups the engine was handed":
  the exception names and messages predate the engine taking groups from any source.
- **Transaction order** (`DeploymentEngine`): every deployment opens `PrepareJournal` in a
  transaction committed **alone** (the journal's schema must exist before it can be read, and DDL
  auto-commits on some engines), then **both** journal queries in one transaction (so the resume
  decision comes from one consistent view), then the deployment itself — committed at plan or
  release boundaries per `DeploymentCommitLevel`, with `ReleaseDeployed` always journaled in the
  same transaction as the release's last plan. Neither the prepare nor the read commit is reported
  to `IDeploymentListener`: they are not deployment progress. On failure the open transaction rolls
  back — earlier commits stand.
- **Steps are verified** (`Deploy( project, groups, verifySteps = true )`; `deploy --no-verify` turns
  it off, for emergencies): each step's `verify.sql` runs right after its `deploy.sql`, in the same
  transaction and with the same timeout and batch splitting, before `StepDeployed` is journaled, so a
  failing check leaves the step undeployed in the journal too. **Only an error fails it** — Sqetch
  reads no results, as it never interprets SQL — and that failure is
  `StepVerificationFailedException`, a `StepScriptFailedException`, so the failure path, the exit
  codes (`51`/`52`) and on-error compensation are exactly a failing deploy script's; on an engine
  that commits DDL implicitly the step is already among those whose deploy started, so it is
  compensated. A **blank** verify script is skipped, not run: new steps start with an empty one and
  SqlClient and MySqlConnector refuse an empty command. Compensation runs no verify scripts. The
  switch is a parameter of `Deploy` rather than the constructor, being a choice about one
  deployment. `IDeploymentListener.VerifyingStep` is a default member, so older listeners compile.
- **Rollback is compensating, not transactional** (`RollbackTrigger`, `deploy --rollback`):
  `on-error` undoes what the run committed after a failure, `on-success` undoes a deployment that
  worked — for proving a bundle applies cleanly, usually a test bundle, though a bundle of
  finalized releases is not refused: what a caller wants to prove and then undo is its own
  business. It runs each step's `rollback.sql` in reverse (releases, then plans, then steps) and
  journals through the `RollingBack*`/`RolledBack*` slots. It is **independent of
  `DeploymentCommitLevel`**, which is the whole point: at plan level the earlier plans are already
  durable and no database transaction could take them back. The engine therefore records committed
  units — a plan moves from pending to committed only when its transaction commits, so a plan the
  open transaction took back is never compensated for a change that never reached the database.
  **Except where the driver says its engine commits DDL implicitly**
  (`IDatabaseDriver.CommitsDdlImplicitly`, MySQL and MariaDB): there a failed transaction's rollback
  may have taken nothing back, so the engine treats what it held as committed — its whole plans,
  and the failed plan cut down to the steps whose deploy script started (`inProgress`), **the failing
  step included**, since it may have half applied — and reports `Committed` to the listener, so the
  CLI exits `52` unless on-error compensation then completes (`51`). Rollback scripts on those
  engines must therefore tolerate a deploy that applied only partly or not at all. A
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
  SqlClient adds the missing '@' itself and ignores unreferenced parameters too. A provider needing
  more goes through `AdoDriverOptions` *with a test proving the need* — so far only SQL Server's
  `ConfigureParameter`, binding a `DateTime` as `datetime2` because SqlClient's default `datetime`
  rounds the instant — and an earlier dialect-plus-SQL-scan was deleted once the probe showed no
  provider needed it. `AdoDriverOptions.SplitStepScript` lets a provider run a step script as several
  batches in its transaction; a failing batch is `StepScriptBatchFailedException`, naming its
  position.
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
- **`sqlserver` driver**: `--sqlserver-host` and `--sqlserver-database` (plus `port`, `username`,
  `password`, `password-env`, `trust-server-certificate`), or `--sqlserver-connection-string`, which
  replaces them. No `username` means integrated authentication. Step scripts split into batches at
  lines whose trimmed content is `GO` (any case; no `GO <count>`), run in order in the step's
  transaction; a script without one runs exactly as written, and journaling never splits on `GO`.
  **SqlClient's Windows native networking library (`Microsoft.Data.SqlClient.SNI.dll`) is not
  shipped**: its proprietary licence terms cannot be met by a GPL/AGPL distribution, so
  `Directory.Build.targets` drops it from every project's output and the factory's static
  constructor sets `Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows`, the managed
  networking Linux and macOS always use. Windows Authentication then works only on domain-joined
  machines. The smoke checks deploy to a port nothing listens on and expect `33` through SqlClient's
  "TCP Provider", which a missing native library would never reach. The template quotes `[plan]`,
  reserved in T-SQL.
- **`mysql` driver** (MySQL and MariaDB): `--mysql-host` and `--mysql-database` (plus `port`,
  `username`, `password`, `password-env`), or `--mysql-connection-string`, which replaces them. The
  connection strings it builds set `AllowUserVariables`, so a step script may use `@variables`
  without MySqlConnector taking them for missing parameters; a supplied string is used as written.
  Journaled instants go to `datetime(6)` as UTC (`timestamp` would convert through the session time
  zone). The template quotes `` `release` ``, reserved there, and declares its index inside the
  table, there being no `create index if not exists`.
- **SQLite, PostgreSQL and SQL Server run DDL inside a transaction**, so a release deployed at
  release commit level really is all-or-nothing on them. **MySQL and MariaDB commit DDL
  implicitly** — a CREATE, ALTER or DROP commits the open transaction, journal rows included — so a
  failing plan leaves earlier steps' changes in place whatever its transaction does. The `mysql`
  driver sets `CommitsDdlImplicitly`, so on-error compensation undoes the failed plan as far as it got
  (see the rollback bullet) and the release then reads as rolled back; without it a rerun restarts
  the failed plan from its first step. Compensation is itself not atomic there, so a failing rollback
  script can leave it half done. Those engines are what make the compensating rollback necessary.
- **Driver tests run against real databases**: SQLite in temp files (never `:memory:`, whose
  contents die with the connection), PostgreSQL, SQL Server, MySQL and MariaDB in Testcontainers
  containers gated by `[DockerFact]`/`[DockerTheory]`, which *skip* where the daemon socket cannot be
  reached rather than failing — SQL Server's by `[DockerAmd64Fact]`, since its image is amd64-only.
  `RealDatabaseDeployment` holds the end-to-end scenarios so every provider runs the same ones, on
  the driver's own built-in template, quoting reserved journal columns through the provider's
  `identifier` function; the `mysql` driver's tests run against MySQL and MariaDB alike through
  `MySqlFamilyServer`, skip only the half-finished-compensation scenario, which implicit DDL
  commits make impossible to hold there, and alone run `OnErrorUndoesAPlanTheServerCommittedPartway`.
  Every scenario step has a passing verify script, and `AFailingVerifyFailsTheStep` takes each provider's
  own run-time failing check (SQLite has `1 / 0` as NULL, MySQL and MariaDB too), proving each provider
  surfaces an error raised while a `select` runs through `ExecuteNonQuery`.
  The scenarios' rollback scripts use `drop table if exists`, as compensation on those engines also
  runs for a step that never applied — the only place the engine's resume decision is read back out of SQL the deployment
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
  decision are exactly what drifts when duplicated. Each host references its own commands project
  (`Pug.Sqetch.Cli.Commands`, `Pug.Sqetch.Deployment.Cli.Commands`). **The two CLIs keep separate
  concerns, and their back ends never depend on each other to do their jobs, but they may share
  dependencies where that makes sense**: 'sqetch-deploy' ships the planning assemblies (engine,
  file-system store), which `bundle` needs to read a project, and 'sqetch' ships the database
  drivers, for the journaling templates they own — it never connects to a database. Shared
  implementations go to `Pug.Sqetch.Shared` rather than into either side. The hosts stay thin, and
  only they reference the concrete database drivers. **The machine formats
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
  is upgrading the tool, not fixing the bundle), `25` the bundle's journaling SQL lacks optional
  statements the command needs (one code for every such command: set them, rebuild), `31`/`32`/`33` unknown driver / driver creation / database unreachable, `41`/`42`/`43` journal
  prepare / query / write, `51`/`52` deployment failed with the database unchanged / left partway,
  `61` rollback failed, `71`/`72`/`73` for `bundle`: project unreadable (missing, damaged, newer
  format) / not bundleable (unset journaling, missing scripts, a `--since` with nothing after it,
  an unknown release) / output not writable (`BundleOutputException`, wrapped by the command so
  no `IOException` elsewhere lands on it), `81` `status --exit-code` found something to deploy (a
  forecast, not a failure; opt-in so `set -e` never trips on a plain `status`), `91` not implemented. Two precedence rules carry the operational meaning:
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
- **A release holds its plans' dependencies, or follows the releases that do**
  (`Project.DetermineMissingDependencies`): a plan's dependency must be in the same release or an
  earlier one in the lineage. `CreateRelease( definition, plans )`, `AddPlanToRelease` and
  `Register` (a plan created into a release) refuse otherwise with
  `MissingPlanDependenciesException`, which names each missing plan and its dependants. Checking
  direct dependencies is enough, since a dependency already in a release was checked when it joined.
  `Register` checks before storing the plan, because the file store does not take part in
  `TransactionScope` and would keep a refused plan. `includeDependencies` (`--with-dependencies`)
  brings along every unreleased plan the added one needs, transitively through unreleased plans
  only; a dependency in a later release is refused, never moved, since moving it could strand
  that release's own plans. `CreateRelease( definition, includeUnreleasedPlans )` needs no check: a
  new release ends the lineage, and every unreleased plan joins it.
- **Names** (`NameRules` in the FileSystem store, surfaced early via CLI `NameValidation`):
  letters, digits and `-_+()@#.`; must start with a letter or digit, must not end with `.`,
  max 128 chars. Names become path segments and git paths.
- **`GetStepScripts` ownership**: the engine returns open streams the caller must dispose,
  and throws `MissingStepScriptsException` when a script file is missing;
  `VerifyStepScripts( plan )` checks a whole plan.

- **Releases are tag-driven and all logic is PowerShell** (`RELEASING.md`): `.github/workflows/release.yml`
  holds only triggers, runners, permissions and artifact plumbing, and every job calls a
  `build.ps1 -Stage` that runs the same on a workstation. `build.ps1` dot-sources `Common.ps1`,
  `Image.ps1` and exactly one platform file, each of which defines `Invoke-PlatformSigning`,
  `New-PlatformPackages` and `Test-Installers`, so no stage branches on the platform. `Image.ps1` is
  no platform file — it needs Docker, not an OS — and serves products whose definition sets `Image`
  (`sqetch-deploy`): `smoke-image` builds the host architecture's image from the archive in `dist/`
  and runs the smoke checks in it as its non-root user; `push-image` builds amd64 and arm64 from both
  Linux archives with no `RUN` step (so no emulation) and pushes one multi-platform image to GHCR and
  Docker Hub (`DOCKERHUB_IMAGE`). The image is the archive's files, never a fresh publish. Smoke checks are written once
  (`Test-ProductCommands`) against a *product invoker*, so the same checks run a binary directly,
  from PATH after an MSI install, or through `docker exec` inside the container a .deb/.rpm was
  installed into. `sqetch-deploy`'s smoke deploys the committed `Installers/smoke/bundle` to SQLite,
  which is what proves the native `e_sqlite3` beside the single file loads, then bundles the
  committed `Installers/smoke/project` (mounted read-only in the containers, and without a
  `.sqetch-user`) and deploys that too, then runs `status --exit-code` on that bundle against a fresh
  database (`81`) and again after deploying it (`0`) — the committed bundle predates `JournalExists`
  (built by 0.1.0, which also proves an older bundle still deploys), so `status` uses the fresh one; regenerate both with `New-SmokeBundle.ps1` whenever the
  project format, the bundle format or the journaling contract changes. The two CLIs release
  independently (`sqetch-v*`, `sqetch_deploy-v*`); the tag's `X.Y.Z` must equal the host project's
  `<Version>`. A `-pre-` tag builds `X.Y.Z-pre.<UTC stamp>` unsigned; a plain tag is signed when all
  the signing variables are set, unsigned (with a warning, and a note in the draft) when none is, and
  refused when only some are.
  Both push the dotnet tool package to nuget.org (`push-tool`, NuGet trusted publishing, so no stored
  key) and, for `sqetch_deploy`, the image (`push-image`; a release is also `latest`) once every smoke
  job passed, and only then create the draft GitHub release. `verify-release.yml` runs when a release
  is published and installs it the ways the README documents — the README's own mise snippet, the
  install action, both registries — on every supported runner, which no local run can; so the README's
  first ```` ```toml ```` block must stay the mise configuration it parses. Every release also carries
  `<name>-<v>.mise.toml` (`mise-config`, `New-MiseConfig`): mise's `http` backend with each
  platform's archive URL and SHA-256, so it needs no GitHub API and works for pre-releases as is. It
  is written in the `release` job, the first holding every platform's archive, before `checksums`
  so `SHA256SUMS` lists it, and `verify-release.yml` installs from it too.
  Formats that cannot carry the suffix keep `X.Y.Z` (MSI ProductVersion) or translate it (deb/rpm
  `~pre…`, which sorts before the release), but every **file name** carries the full version as
  written, because GitHub rewrites `~` in asset names and `SHA256SUMS` must match them.

## Conventions

- Tabs for indentation; spaces inside parentheses: `Foo( bar, baz )`; file-scoped namespaces.
- **Namespaces follow the project name and folder**: a production project's namespace is its name
  with any `.Common` or `.Abstractions` suffix dropped, plus the folder path — so
  `Pug.Sqetch.Cli.Common` is `Pug.Sqetch.Cli`, `Pug.Sqetch.Deployment.Bundling.Abstractions` is
  `Pug.Sqetch.Deployment.Bundling`, and `Pug.Sqetch.Planning.Abstractions`' `ProjectInfoStore/` folder is
  `Pug.Sqetch.Planning.ProjectInfoStore`. Each
  project's `RootNamespace` encodes the rule, so Rider's *Adjust Namespaces* keeps files in line. The
  sqetch CLI imports its `Rows` helpers through an alias because `Spectre.Console.Rows` shares the name.
- Exceptions: plain classes with get-only properties and an optional message via `base(...)`
  (see `UnknownPlanException`); mapped to friendly CLI messages in `CliErrors.Describe`,
  surfaced by the central `SetExceptionHandler` in `SqetchApp` with exit code 1.
- Tests: xunit. sqetch CLI tests drive the real command tree and composition through
  `SqetchTester` (a `CommandAppTester` over `SqetchApp.CreateDefaultRegistrar`, with an empty
  user-level template root and a captured warning console) and switch the process-wide current directory — every test class that does so must join
  `[Collection( "cli" )]` to stay sequential. sqetch-deploy CLI tests instead pass absolute
  paths and inject fakes via the production `TypeRegistrar` (`CommandAppTester( registrar )`),
  so they need no collection — except the one test of `bundle`'s default output name, which must
  switch the current directory and runs alone in the `"cwd"` collection (`DisableParallelization`).
  `bundle` tests build their projects through the engine (`BundleTestProjects`), not the 'sqetch'
  CLI. Store/engine tests build on `TempProject`,
  `TestData` and `ShardingCases` from `src/ProjectInfoStores/Pug.Sqetch.Tests.Planning.Stores.FileSystem/TestSupport.cs`;
  git legibility is asserted with real `git` via the `Git` helper.
- Verify changes with the full `dotnet test` plus, for user-visible behavior, a live CLI run
  in a temp directory (`project user`, `project init`, then the flow under test).
- **The agent skills document the CLIs for their users** (`claude/pug-sqetch/skills`). A change to a
  command, an option, an exit code, the journaling contract, the deploy semantics or the install
  channels updates the skills **in the same commit**. A new feature is marked "since X.Y" there,
  since users' repositories pin older versions, and the skills tell agents to trust the installed
  tool's `--help` over them. Bump `version` in `claude/pug-sqetch/.claude-plugin/plugin.json`
  whenever a skill changes: Claude Code updates an installed plugin only when that version
  changes. The journaling templates in `sqetch-new-project/references` must stay correct after
  rollbacks; re-run their scenarios (fresh, redeploy, `on-success` then redeploy, `on-error` at
  plan level, partway then a partial rollback, continuation) on SQLite and PostgreSQL when they or
  the deploy semantics change. Users get the skills from `main`, so they change for users only when
  `main` moves to a release.
