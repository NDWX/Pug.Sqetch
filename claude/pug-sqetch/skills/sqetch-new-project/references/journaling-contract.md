# The journaling contract

The project's journaling SQL is how `sqetch-deploy` records a deployment and learns where a
database stands. Sqetch never generates it, so it can write to any table the project likes. Fifteen
slots are required before `bundle` will run; a sixteenth, `JournalExists` (since 0.2), is optional
and needed only by `sqetch-deploy status`.

## Slots and their parameters

`sqetch journaling parameters` prints this table from the installed tool; prefer it to this copy.

| Slot | Kind | Parameters |
|---|---|---|
| `PrepareJournal` | setup | `@project` |
| `GetLatestRelease` | query | `@project` |
| `GetDeployedPlans` | query | `@project @release` |
| `JournalExists` (optional) | query | `@project` |
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

  More than one row is an error.
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

## Changing the SQL later

Journaling statements are not frozen with releases: change them with `sqetch journaling set` at any
time. A bundle carries the SQL it was built with. Changing the table's shape for databases already
journaling into it is the user's migration to plan; ask before doing it.
