# The journaling contract

The project's journaling SQL is how `sqetch-deploy` records a deployment and learns where a
database stands. Sqetch never generates it, so it can write to any table the project likes. Fifteen
slots are required before `bundle` will run. Three more are optional (since 0.2): `JournalExists`,
which `sqetch-deploy status` needs, and `AcquireLock`/`ReleaseLock`, which stop two deployments to
one database running at once.

## Slots and their parameters

`sqetch journaling parameters` prints this table from the installed tool; prefer it to this copy.

| Slot | Kind | Parameters |
|---|---|---|
| `PrepareJournal` | setup | `@project` |
| `GetLatestRelease` | query | `@project` |
| `GetDeployedPlans` | query | `@project @release` |
| `JournalExists` (optional) | query | `@project` |
| `AcquireLock` (optional) | lock query | `@project` |
| `ReleaseLock` (optional) | lock | `@project` |
| `DeployingRelease` | write | `@project @release @description @utcTimestamp` |
| `DeployingPlan` | write | `@project @release @plan @description @utcTimestamp` |
| `DeployingStep` | write | `@project @release @plan @step @description @utcTimestamp` |
| `StepDeployed` | write | `@project @release @plan @step @description @utcTimestamp` |
| `PlanDeployed` | write | `@project @release @plan @description @utcTimestamp` |
| `ReleaseDeployed` | write | `@project @release @description @utcTimestamp` |
| `RollingBackRelease` | write | `@project @release @utcTimestamp` |
| `RollingBackPlan` | write | `@project @release @plan @utcTimestamp` |
| `RollingBackStep` | write | `@project @release @plan @step @utcTimestamp` |
| `RolledBackStep` | write | `@project @release @plan @step @utcTimestamp` |
| `RolledBackPlan` | write | `@project @release @plan @utcTimestamp` |
| `RolledBackRelease` | write | `@project @release @utcTimestamp` |

- Write parameters as `@name`. Every parameter is bound on every statement of its slot, so a
  statement may use any of them, or none.
- `@utcTimestamp` is the deploying host's clock, as a UTC date-time, read once per event. Store it
  in a time-zone-aware column (`timestamptz` on PostgreSQL), or use the server's own `now()` instead.
- Unreleased plans deploy under the release name `""` (an empty string, not NULL), so the
  `release` column must accept it.

## Statements and transactions

- A slot may hold several statements, separated by a line whose trimmed content is exactly `;;`.
  That is the only parsing Sqetch does. Query slots hold exactly one statement.
- `PrepareJournal` runs at the start of **every** deployment, in a transaction committed alone. Make
  it idempotent (`create table if not exists`).
- The two queries run together, in one transaction, before anything deploys. `status` runs
  `JournalExists` and then them, in one transaction it rolls back.
- The write slots run in the same transaction as the work they record. A failure rolls back both,
  so the journal never claims what did not happen.

## What the queries must return

Columns are read by position; extra columns are ignored.

- **`GetLatestRelease`**: zero rows for a database never deployed to, or one row:
  1. the release name the database last reached (never NULL; `""` is the unreleased plans);
  2. whether that release was completed: a boolean, any number (non-zero is true), or text
     `1`/`y`/`yes`/`t`/`true`.

  More than one row is an error. A release a rollback undid entirely is not the one reached; one a
  rollback has started undoing (`RollingBackRelease`) is not completed until it is deployed again,
  even when the rollback stopped partway.
- **`GetDeployedPlans`**: the names of the plans of `@release` that are in place, one per row, in
  any order.
- **`JournalExists`** (optional, since 0.2): one row whose first column says whether the journal
  exists yet (coerced like `completed`); false or no row means it does not. Check the catalog, not
  the table itself: a query against a missing table fails, which is the case it exists to answer.
  `sqetch-deploy status` runs it instead of `PrepareJournal`, so it never creates the journal, and
  reads a database whose journal does not exist as never deployed to.

**Rollbacks must be accounted for.** `--rollback` journals `RollingBack*`/`RolledBack*` events and
undoes the work. Queries that only look at `DeployingRelease`, `ReleaseDeployed` and `PlanDeployed`
will then misreport the database: "nothing to deploy" after an `on-success` rollback, or plans
skipped because they once deployed. A rollback after a failure undoes only what that run committed:
plans an earlier run deployed in the same release stay in place, even though `RolledBackRelease`
is journaled. The templates handle all of this. Keep their query logic when adapting them.

## The deployment lock (optional, since 0.2)

With `AcquireLock` set, every deployment takes the database's lock first, before `PrepareJournal`,
and refuses to run when another deployment holds it: exit `34`, nothing changed, safe to retry
later. `ReleaseLock` gives it back when the deployment ends, success or failure, rollbacks included.
Without them a deployment runs unguarded, as before 0.2.

- **`AcquireLock`** is a query: one row whose first column is true when the lock was taken (coerced
  like `completed`); false or no row means another deployment holds it.
- Use a **session-level** lock: `AcquireLock` runs in a transaction committed on its own, and the lock
  must outlive it. A session lock also ends with its database session, so a deployment that crashes
  does not leave the database locked for long: the server ends the session when the connection
  drops, though a host that vanishes without closing it (a CI runner VM gone) holds it until TCP
  keepalive notices, which can take hours. Every deployment meanwhile exits `34`. To end it, find
  the holder and end its session: PostgreSQL `pg_locks` where `locktype = 'advisory'`, then
  `pg_terminate_backend( pid )`; SQL Server `sys.dm_tran_locks` where `resource_type = 'APPLICATION'`,
  then `KILL <session>`; MySQL and MariaDB `is_used_lock( <name> )`, then `KILL <id>`.
- **Session locks need a session.** Behind a pooler in transaction mode (PgBouncer, Supavisor or
  similar on PostgreSQL), each transaction may run on a different server connection, so the lock
  is taken and released on different sessions and guards nothing. Connect directly, or through
  session pooling, or leave `AcquireLock`/`ReleaseLock` unset there.
- **Take it without waiting**, so a second deployment fails fast rather than queueing. Name it after
  `@project`, so projects sharing a database do not block each other.
- The templates: PostgreSQL `pg_try_advisory_lock`, SQL Server `sp_getapplock` with
  `@LockOwner = 'Session'` and `@LockTimeout = 0`, MySQL and MariaDB `get_lock(…, 0)` (lock names are
  server-wide there, so the name includes `database()`).
- **SQLite has no session locks**, so its template sets neither. Each SQLite transaction takes the
  write lock, but two deployments can still interleave between commits: run them one at a time.
- `sqetch-deploy status` takes no lock; while a deployment runs it may show a release partway.

## Changing the SQL later

Journaling statements are not frozen with releases: change them with `sqetch journaling set` at any
time. A bundle carries the SQL it was built with. Changing the table's shape for databases already
journaling into it is the user's migration to plan; ask before doing it.
