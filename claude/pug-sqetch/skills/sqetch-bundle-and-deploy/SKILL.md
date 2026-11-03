---
name: sqetch-bundle-and-deploy
description: Use when building a Sqetch bundle (sqetch-deploy bundle) or deploying one to a database (sqetch-deploy deploy), or previewing a deployment (sqetch-deploy status), i.e. choosing between a test bundle, a release bundle and a continuation bundle, the bundle type, the SQLite or PostgreSQL driver and its connection switches, commit level, step timeout, rollback on error or on success, logging, how the journal decides what deploys, and what an exit code means and whether to retry.
---

# Bundling and deploying

All three are `sqetch-deploy` commands. Check its version and `--help` first (skill `sqetch`). Neither
needs a user identity.

## Bundle

A bundle is a deployable snapshot of the project: the plans to deploy, in order, with their scripts,
the release lineage and the project's journaling SQL. Build it once and deploy the same file to every
environment.

```sh
sqetch-deploy bundle --project db -o release-1.4.zip               # finalized releases (the default)
sqetch-deploy bundle --project db --test -o test.zip               # everything, for testing
sqetch-deploy bundle --project db --since 1.4 -o after-1.4.zip     # only releases after 1.4
```

| Option | Meaning |
|---|---|
| `--project <DIR>` | the project folder; defaults to the current directory |
| `--finalized` | only plans of finalized releases (the default): what ships |
| `--test` | every plan: finalized and open releases, and unreleased plans |
| `--since <RELEASE>` | a *continuation bundle*: only the releases after the named one, for databases that have it |
| `-t, --bundle-type` | `zip` (default), `tar`, `tar.gz` or `directory` |
| `-o, --bundle-output-path` | the file or folder to write; it must not exist. Defaults to `<project>-<timestamp>` |

- Always pass `-o`, so the bundle's path is known without reading the output.
- `bundle` refuses (exit `72`) until all fifteen journaling statements are set, and names the
  missing ones; see skill `sqetch-new-project`. It also refuses a missing step script, a `--since`
  naming an unknown release, and a bundle that would be empty.
- `bundle` only reads the project. Run it from a clean checkout of the commit being built.

## Deploy

```sh
sqetch-deploy deploy release-1.4.zip --driver postgres \
  --postgres-host db.example.com --postgres-database orders --postgres-username deployer \
  --postgres-password-env ORDERS_DB_PASSWORD
sqetch-deploy deploy test.zip --driver sqlite --sqlite-file test.db --rollback on-success
```

| Option | Meaning |
|---|---|
| `<BUNDLE>` | the bundle file or folder |
| `-d, --driver <NAME>` | `sqlite`, `postgres` or, since 0.2, `sqlserver` and `mysql` (`sqetch-deploy drivers list`) |
| `--<driver>-<parameter> <value>` | the driver's connection switches; see `references/drivers.md` or `sqetch-deploy drivers parameters <driver>`. A value starting with `-` is written `--<driver>-<parameter>=<value>` |
| `--commit-level <LEVEL>` | `release` (default): each release commits whole. `plan`: commit after every plan, so a failure keeps the plans before it |
| `--rollback <MODE>` | `on-error`: undo what this run committed if it fails. `on-success`: undo everything after it succeeds, to prove a bundle on a throwaway database. Default: none |
| `--no-verify` | since 0.2: do not run the steps' `verify.sql`. For emergencies only |
| `--timeout <SECONDS>` | per step script (deploy and verify each); default 300, `0` for none |
| `--log <PATH>` | append timestamped progress lines to a file |
| `-t, --bundle-type` | inferred from the path; needed only for an unusual file name |

- Pass passwords through `--postgres-password-env <VARIABLE>`, never `--postgres-password`: a
  command line is visible to every process on the host.
- Confirm the target database with the user before deploying. `--rollback on-success` is for
  throwaway databases only.
- The `rollback` command is not implemented yet (exit `91`). Undoing is done through
  `deploy --rollback`.
- Since 0.2, each step's `verify.sql` runs right after its `deploy.sql`, in the same transaction
  (the output shows "verifying step …"). An error from it fails the step: exit `51`/`52`, and
  `--rollback on-error` undoes it as for any failure. On MySQL and MariaDB the step's schema changes
  committed before the verify ran, so only `--rollback on-error` takes them back. Only errors fail it; a blank `verify.sql` is
  skipped. A project whose verify scripts never ran before may start failing on upgrade: fix the
  script, or, to get a deployment out in an emergency, pass `--no-verify`. Writing verify scripts:
  skill `sqetch-plans-and-releases`.

## Status: what a deploy would do (since 0.2)

```sh
sqetch-deploy status release-1.4.zip --driver postgres \
  --postgres-host db.example.com --postgres-database orders --postgres-username deployer \
  --postgres-password-env ORDERS_DB_PASSWORD
```

It takes the same bundle, `--driver` and driver switches as `deploy`, and changes nothing: it runs
only the journal's queries, in a transaction it rolls back, never `PrepareJournal` or a step. It
prints where the database is (`fresh` when it has no journal, `empty`, or a release `partial` or
`complete`), the releases that would deploy (whether each resumes, and how many plans it has left;
one with none left still has its completion to record), then the plans that would deploy, by
release, in order, or "nothing to deploy". A bundle that does not fit is refused with `23`, as
`deploy` would refuse it. It takes no `deploy`-only option (`--log`, `--commit-level`,
`--rollback`, `--no-verify`, `--timeout`).

- Run it before a production deploy, and show the user what it lists before deploying.
- `-o json` or `-o csv` for a pipeline. `--exit-code` makes it exit `81` when something would
  deploy (and `0` when not), so a pipeline can branch without parsing output.
- It needs the project's optional `JournalExists` journaling statement, and refuses a bundle
  without it (exit `25`): set it (skill `sqetch-new-project`) and rebuild the bundle.
- With the `mysql` driver, name the database (`--mysql-database`, or `Database=` in a connection
  string): the template's `JournalExists` looks in the current database, so without one `status`
  reports a fresh database.
- The sqlite driver creates a missing database file. To leave a missing file alone, pass
  `--sqlite-connection-string "Data Source=<file>;Mode=ReadOnly"`.

## Concurrent deployments (since 0.2)

When the project's journaling SQL sets the optional `AcquireLock`/`ReleaseLock` (the PostgreSQL,
SQL Server and MySQL templates do), each deployment takes the database's lock before anything else
and prints "acquired the deployment lock". A second deployment to the same database while one runs
is refused at once with exit `34`, having changed nothing; retry it when the first finishes. The
lock is released when the deployment ends, failures and rollbacks included, and in any case when
`sqetch-deploy` exits. SQLite sets no lock: run SQLite deployments one at a time. `status` takes no
lock, so during a deployment it may show a release partway.

## How the journal decides what deploys

Each database's journal, read through the project's journaling SQL, decides where a deployment
starts. There is nothing to configure:

- **A database never deployed to** gets the whole bundle.
- **A release fully deployed** is skipped. Deployment continues with the release that builds on it.
- **A release partly deployed**, after a failure, resumes inside it: the plans already in place are
  skipped and the rest are deployed.
- **Nothing new** prints "nothing to deploy" and exits `0`. Deploying the same bundle twice is safe.
- **A continuation bundle** (`--since`) must start with the release after the database's latest.
- **A bundle that does not fit** the database is refused before anything runs (exit `23`), for
  example a bundle from another lineage, or one that skips a release the database lacks.

Consequences:

- **Test databases are single-use.** A release already deployed to a database is never deployed
  there again, so plans added to an open release later never reach it. A database that received
  unreleased plans from a `--test` bundle cannot take a release bundle. Use a fresh database for
  each test run, or `--rollback on-success`.
- **Retrying is safe.** After a failure, fix the cause and deploy the same bundle again; it resumes.
- **Commit level decides what a failure keeps.** At `release` level, a failing release leaves no
  trace of itself; earlier releases in the same run stay. At `plan` level, every completed plan
  stays. `--rollback on-error` undoes this run's committed work in either case.

## Exit codes

The tens digit is the category; the full table, with what to do for each, is
`references/exit-codes.md`. The ones to know:

| Code | Meaning |
|---|---|
| `0` | deployed, nothing to deploy, or deployed and undone on request |
| `10` | command-line mistake |
| `23` / `24` | the bundle does not fit this database / needs a newer `sqetch-deploy` |
| `25` | the bundle's journaling SQL lacks a statement the command needs (`status`: `JournalExists`) |
| `33` | database unreachable |
| `34` | another deployment holds the database's lock; nothing changed, retry later |
| `41`–`43` | the journaling SQL failed |
| `51` | a step failed; the database is as it was |
| `52` | a step failed partway; earlier work stayed committed |
| `61` | a rollback script failed; the database is half undone and needs a person |
| `71`–`73` | `bundle` failed: project unreadable / not bundleable / output not writable |
| `81` | `status --exit-code`: something would deploy |
