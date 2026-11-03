---
name: sqetch-plans-and-releases
description: Use when making a database schema change in a Sqetch project, i.e. turning a task, ticket or pull request into a plan, splitting it into steps and writing their deploy, verify and rollback scripts, declaring dependencies between plans or steps, assembling plans into a release for a software version, finalizing it when the version ships, fixing something after a release shipped, inspecting plans, steps and releases, or resolving a plan-index merge conflict.
---

# Plans, steps and releases

Run `sqetch` from the project folder (the one holding `sqetch-project.json`). Check the installed
version and its `--help` first (skill `sqetch`).

## How the work maps

| The work | In Sqetch | Command |
|---|---|---|
| A task, ticket or pull request needing schema changes | a **plan**, named after it (`orders-1234`, `add-orders`) | `sqetch plan create -n <plan>` |
| One reviewable change within it (a table, an index, a backfill) | a **step** of that plan | `sqetch plan add-step -p <plan> -n <step>` |
| A software version being prepared | an **open release**, gathering the plans that ship in it | `sqetch release create -n <release> …` |
| The version ships | the release is **finalized**: frozen, with its plans | `sqetch release finalize -n <release>` |
| A fix after the version shipped | a **new plan**, in the next release | never edit the finalized one |

A plan stays **unreleased** while its task is in progress, so it is deployed by test bundles but
not by release bundles (skill `sqetch-bundle-and-deploy`).

## Creating a plan and its steps

```sh
sqetch plan create -n add-orders -d "Orders table and its index"
sqetch plan add-step -p add-orders -n orders-table
sqetch plan add-step -p add-orders -n orders-customer-index --requires orders-table
```

`add-step` prints the paths of the step's three scripts. Write them:

- **`deploy.sql`** makes the change. It may hold several statements. It runs in a transaction with
  the journal entry recording it, so on SQLite, PostgreSQL and SQL Server a failing step leaves nothing behind. MySQL and
  MariaDB commit DDL implicitly: there write `rollback.sql` to tolerate a partly applied deploy
  (`drop table if exists`) and deploy with `--rollback on-error` (skill `sqetch-bundle-and-deploy`,
  drivers).
  Do not add `begin`/`commit`.
- **`rollback.sql`** undoes exactly what `deploy.sql` did, in reverse order (drop what was created,
  restore what was changed). `sqetch-deploy deploy --rollback` runs it, so it must work against
  the state `deploy.sql` leaves. Where data would be lost, say so in the script's comments and to the
  user.
- **`verify.sql`** checks the change is in place. It is bundled but not run yet; write it anyway
  (a `select` that fails when the change is missing).

Write SQL for the project's engine (the `engine` in `sqetch-project.json` names it). A step should be
small enough to review on its own; split a plan into steps rather than writing one large script.

## Dependencies

- **Between steps** of one plan: `--requires a,b` on `add-step`. Steps deploy in dependency order.
- **Between plans**: `--require-plans a,b` on `plan create`, when a plan builds on another (a
  foreign key to a table another plan creates). Plans deploy in dependency order.
- **A release must contain its plans' dependencies**, or a release before it must. Since 0.1.1,
  `release create --plans`, `plan set-release` and `plan create -r` refuse a plan whose dependency
  is unreleased or in a later release, naming what is missing. Name the dependencies in `--plans`
  too, or add the plan with `plan set-release -p <plan> -r <release> --with-dependencies`, which
  brings every unreleased plan it needs, directly or indirectly, into the same release. A
  dependency in a later release is refused, not moved: move it with `plan set-release` first.
  `--all-unreleased` cannot leave one behind.
- **With `sqetch` 0.1.0**, none of this is enforced and `--with-dependencies` does not bring the
  plan's own dependencies along. There, add each dependency yourself, and before finalizing check
  `sqetch plan list -r <release> -o json` (the `dependencies` field): each dependency must be in this
  release or an earlier one.

## Releases

```sh
sqetch release create -n 1.4 --all-unreleased                  # the first release
sqetch release create -n 1.5 --depends-on 1.4 --plans add-orders,add-customers
sqetch plan set-release -p add-invoices -r 1.5 --with-dependencies   # with its unreleased dependencies
sqetch plan set-release -p add-invoices --unreleased              # take it back out
sqetch release finalize -n 1.5
```

- Releases form one line: every release after the first names its predecessor with
  `--depends-on`, and a release can have only one successor. Name releases after the software
  versions they ship with.
- `--all-unreleased` takes every unreleased plan; `--plans` names them. A plan belongs to one release
  at a time; `set-release` moves it.
- Create the release when the version is being assembled, deploy it to test environments while it
  is open, and **finalize it when the version ships**, not before: after finalizing, its plans
  and scripts cannot change. Ask the user before finalizing.
- `sqetch release delete -n <release>` deletes an open release and returns its plans to unreleased.
- Releases already deployed to a database stay deployed there. Plans added to an open release after
  it reached a database are not deployed to that database (skill `sqetch-bundle-and-deploy`), so
  treat a test database as single-use.

## Inspecting

```sh
sqetch plan list                          # unreleased plans
sqetch plan list --released -o json       # plans in releases
sqetch plan list -r 1.5
sqetch plan list-steps -p add-orders      # steps in deployment order
sqetch release list                       # open releases
sqetch release list --finalized
```

Lists come back in deployment order. `-o json` or `-o csv` give data to parse.

## Deleting

`sqetch plan delete -n <plan>` and `sqetch plan delete-step -p <plan> -n <step>` remove work that
has not shipped. Ask before deleting anything, and never delete what a finalized release holds.

## Names

Plans, steps and releases: letters, digits and `-_+()@#.`, starting with a letter or digit, not
ending with `.`, at most 128 characters. Names become folder names and git paths.

## Branches and merges

The project is plain files, so plans are branched, reviewed and merged with the code. Two branches
that each create a plan both change `plan-index`, a derived file. Do not resolve a conflict there by
hand: rebuild it from the plan folders, which works with the conflict markers still in it, and
commit the result:

```sh
sqetch project reindex
git add plan-index
```

Conflicts inside a plan's own files are ordinary SQL merges.
