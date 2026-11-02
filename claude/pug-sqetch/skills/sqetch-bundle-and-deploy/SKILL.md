---
name: sqetch-bundle-and-deploy
description: Use when building a Sqetch bundle (sqetch-deploy bundle) or deploying one to a database (sqetch-deploy deploy), i.e. choosing between a test bundle, a release bundle and a continuation bundle, the bundle type, the SQLite or PostgreSQL driver and its connection switches, commit level, step timeout, rollback on error or on success, logging, how the journal decides what deploys, and what an exit code means and whether to retry.
---

# Bundling and deploying

Both are `sqetch-deploy` commands. Check its version and `--help` first (skill `sqetch`). Neither
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
| `-d, --driver <NAME>` | `sqlite` or `postgres` (`sqetch-deploy drivers list`) |
| `--<driver>-<parameter> <value>` | the driver's connection switches; see `references/drivers.md` or `sqetch-deploy drivers parameters <driver>`. A value starting with `-` is written `--<driver>-<parameter>=<value>` |
| `--commit-level <LEVEL>` | `release` (default): each release commits whole. `plan`: commit after every plan, so a failure keeps the plans before it |
| `--rollback <MODE>` | `on-error`: undo what this run committed if it fails. `on-success`: undo everything after it succeeds, to prove a bundle on a throwaway database. Default: none |
| `--timeout <SECONDS>` | per step script; default 300, `0` for none |
| `--log <PATH>` | append timestamped progress lines to a file |
| `-t, --bundle-type` | inferred from the path; needed only for an unusual file name |

- Pass passwords through `--postgres-password-env <VARIABLE>`, never `--postgres-password`: a
  command line is visible to every process on the host.
- Confirm the target database with the user before deploying. `--rollback on-success` is for
  throwaway databases only.
- The `rollback` command is not implemented yet (exit `91`). Undoing is done through
  `deploy --rollback`.

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
| `33` | database unreachable |
| `41`–`43` | the journaling SQL failed |
| `51` | a step failed; the database is as it was |
| `52` | a step failed partway; earlier work stayed committed |
| `61` | a rollback script failed; the database is half undone and needs a person |
| `71`–`73` | `bundle` failed: project unreadable / not bundleable / output not writable |
