---
name: sqetch-plans-and-releases
description: Use when making a database schema change in a Sqetch project, i.e. turning a task, ticket or pull request into a plan, splitting it into steps and writing their deploy, verify and rollback scripts, declaring dependencies between plans or steps, assembling plans into a release for a software version, finalizing it when the version ships, fixing something after a release shipped, inspecting plans, steps and releases, resolving a plan-index merge conflict, or checking a project for inconsistent data (sqetch project check).
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
  restore what was changed). `sqetch-deploy deploy --rollback` and, since 0.2,
  `sqetch-deploy rollback` run it, so it must work against the state `deploy.sql` leaves. Where data would be lost, say so in the script's comments and to the
  user.
- **`verify.sql`** checks the change is in place. Since `sqetch-deploy` 0.2 it runs right after
  `deploy.sql`, in the same transaction, and **fails the step only by raising an error**: Sqetch
  reads no results, so a `select` returning `false`, `0` or no rows passes. Select what the deploy
  created (`select id, customer_id from orders where 1 = 0` fails if the table or a column is
  missing); to check a condition, make the SQL error when it does not hold (see "Verify scripts"
  below). A failing verify fails the step exactly like a failing deploy script: the same exit codes,
  and the same rollback (`--rollback on-error` undoes the step on MySQL and MariaDB). A blank
  `verify.sql` is skipped; `deploy --no-verify` skips them all, for emergencies. Earlier versions
  bundle it without running it.

Write SQL for the project's engine (the `engine` in `sqetch-project.json` names it). A step should be
small enough to review on its own; split a plan into steps rather than writing one large script.

### Verify scripts

Since `sqetch-deploy` 0.2 a verify script passes unless it raises an error. Two kinds of check:

- **The change exists**: select what the deploy created, so a missing table or column is an error.
  `select id, customer_id from orders where 1 = 0` works on every engine.
- **A condition holds**: turn "false" into a run-time error. Plain `select count(*) = 1 …` passes
  whatever it returns. Each check below fails while `orders` is empty, tested on its engine:

| Engine | Fails when `orders` is empty |
|---|---|
| PostgreSQL, SQL Server | `select 1 / count(*) from orders` (division by zero) |
| MySQL, MariaDB (`1 / 0` is NULL there) | `select ( select 1 union all select 2 where not exists ( select 1 from orders ) )` (subquery returns more than one row) |
| SQLite (`1 / 0` is NULL there) | `select case when exists ( select 1 from orders ) then 1 else json( 'x' ) end` (malformed JSON) |

Keep verify scripts read-only: they run in the deployment's transaction.

## Dependencies

- **Between steps** of one plan: `--requires <step>` on `add-step`. Steps deploy in dependency order.
  Since 0.2 a step requires at most one step, so a plan's steps form a chain that `move-step` can
  reorder (see Editing); `sqetch` 0.1.x accepted a list.
- **Between plans**: `--require-plans a,b` on `plan create`, when a plan builds on another (a
  foreign key to a table another plan creates). Plans deploy in dependency order.
- **A release must contain its plans' dependencies**, or a release before it must. Since 0.1.1,
  `release create --plans`, `plan set-release` and `plan create -r` refuse a plan whose dependency
  is unreleased or in a later release, naming what is missing. Name the dependencies in `--plans`
  too, or add the plan with `plan set-release -p <plan> -r <release> --with-dependencies`, which
  brings every unreleased plan it needs, directly or indirectly, into the same release. A
  dependency in a later release is refused, not moved: move it with `plan set-release` first.
  `--all-unreleased` cannot leave one behind.
- **Moving a plan between releases** (`plan set-release -p <plan> -r <release>` on a released plan):
  since 0.2 it checks first and moves in one step, so a refused move leaves the plan where it was.
  Besides its dependencies, it refuses when a plan depending on this one is in a release *before* the
  target, naming it: move that dependant first, then this plan. Dependants already in the target or
  a later release are fine. `--with-dependants` is for `--unreleased` only, and `--with-dependencies`
  for `-r` only; either one in the wrong direction is refused.
- **With `sqetch` 0.1.x**, a refused move has already taken the plan out of its old release and
  leaves it unreleased, without saying so. After a refused `set-release` there, check
  `sqetch plan --name <plan>` and add it back to its release.
- **With `sqetch` 0.1.0**, none of this is enforced and `--with-dependencies` does not bring the
  plan's own dependencies along. There, add each dependency yourself, and before finalizing check
  `sqetch plan list -r <release> -o json` (the `dependencies` field): each dependency must be in this
  release or an earlier one.
- **Reviewing them** (since 0.2): `sqetch plan graph -r <release>` lists the release's plans with
  every dependency's release, so one outside it stands out (see Inspecting).

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
  at a time; `set-release` moves it (see Dependencies for what a move checks).
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
sqetch plan graph                         # dependency graph, plans grouped by release (since 0.2)
sqetch plan graph -r 1.5                  # one release; dependencies elsewhere show their release
sqetch plan graph --from-release 1.5      # 1.5, every later release, and the unreleased plans
sqetch plan graph --unreleased --format mermaid
```

Lists come back in deployment order. `-o json` or `-o csv` give data to parse.

`plan graph` prints text by default (`orders: requires base (1.0)`), or `--format mermaid` or
`--format dot` (Graphviz), with arrows from a dependency to the plan needing it and a dependency
outside the selection drawn dashed. `-r` and `--unreleased` combine; `--from-release` stands alone.
To show the graph in a pull request or issue, paste the Mermaid output inside a ```` ```mermaid ````
fenced block, which GitHub renders.

## Editing

Since 0.2, plans, steps and releases can be changed in place, keeping their history and creation
details (with 0.1.x, delete and recreate instead):

```sh
sqetch plan update -n add-orders -d "Orders, their lines and index"   # the description
sqetch plan set-dependencies -n add-orders --plans add-customers       # replace them ("" for none)
sqetch plan add-dependency -n add-orders --plans add-products
sqetch plan remove-dependency -n add-orders --plans add-customers     # refused if not a dependency
sqetch release update -n 1.5 -d "Ordering"
sqetch plan update-step -p add-orders -n orders-table -d "Creates the table"
sqetch plan rename-step -p add-orders -n orders-table --new-name create-orders   # prints the new path
sqetch plan move-step -p add-orders -n orders-customer-index --after orders-lines
sqetch plan move-step -p add-orders -n orders-customer-index --before orders-lines
```

- **`set-dependencies`, `add-dependency` and `remove-dependency`** check a new dependency as
  `plan create` does: it must exist, must not lead back to the plan (refused, naming the loop), and
  for a released plan must be in its release or an earlier one.
- **`move-step`** reorders through dependencies, like moving an item in a linked list: the steps
  that required the moved step take its dependency, then it requires the step it is placed after
  (whose other followers now require it), or takes the dependency of the step it is placed before
  (which then requires it). It prints each step whose dependency changed
  (`c: requires a (was b)`); read it, since a move can take a dependency away from a step.
- **`rename-step`** moves the step's folder, scripts included, and the steps requiring it follow.
- Every edit is checked in full before anything is written, and a command cut short leaves a valid
  project: rerun it, or run `sqetch project reindex` after an interrupted `rename-step`.
- Edits are refused in a finalized release. A change there is a new plan in a later release.

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

Since 0.2, `reindex` also repairs a plan whose folder and `plan.json` disagree about its release
(the folder wins), which a `sqetch` command interrupted mid-move can leave behind, and a step whose
folder and `step.json` disagree about its name, which an interrupted `rename-step` can leave. Run
it, then check `sqetch plan --name <plan>` or `sqetch plan list-steps -p <plan>`.

## Checking a project (since 0.2)

```sh
sqetch project check            # report, change nothing
sqetch project check --fix      # rebuild the plan index first, then report what is left
sqetch project check -o json    # for a pipeline
```

It needs no user identity and changes nothing without `--fix`. It exits `0` when it finds nothing,
`2` when it finds something (one row per finding: check, release, plan, step, problem), and `1` when
it could not run. Run it after a merge, after hand edits, before finalizing a release, and in CI.

| Check | Means | Fix |
|---|---|---|
| `index-missing`, `index-unreadable`, `index-drift`, `plan-release-mismatch` | `plan-index` or a `plan.json` disagrees with the plan folders | `--fix` (or `project reindex`) |
| `step-name-mismatch`, `step-dependency-unknown` | left by an interrupted `rename-step` | `--fix` |
| `plan-name-mismatch` | a `plan.json` names a plan other than its folder | rename the folder or fix the file by hand |
| `plan-duplicate`, `release-duplicate` | the same plan or release in two folders (or two release names differing only in case), as a mangled merge or a copied folder leaves | keep one; ask the user which |
| `release-name-mismatch` | a `release.json` names a release other than its folder | fix by hand; ask the user |
| `document-unreadable` | a `plan.json`, `step.json` or `release.json` cannot be parsed, often merge-conflict markers | resolve the conflict in that file |
| `orphan-folder` | a plan folder without `plan.json`, or a release folder without `release.json`: every command ignores it and what is in it | restore the file, or remove the folder if it is debris |
| `invalid-name` | a project, release, plan or step name the naming rules reject | recreate it under a valid name |
| `released-plan-missing-dependency` | a released plan's dependency is unreleased or in a later release (0.1.0 allowed this) | `plan set-release` the dependency into the release or an earlier one; in a finalized release, a new plan |
| `plan-dependency-unknown`, `plan-dependency-cycle` | a plan depends on a plan that does not exist, or plans depend on each other in a circle | `plan set-dependencies` / `remove-dependency` |
| `release-lineage` | the releases do not form one line: two dependants, a missing predecessor, two starts, a cycle, or an open release before a finalized one | needs a person; ask the user |
| `step-scripts-missing` | a step lacks `deploy.sql`, `verify.sql` or `rollback.sql` | create the file (an empty `verify.sql` is fine) |
| `step-multiple-dependencies` | a step requires more than one step (allowed before 0.2) | `plan move-step` |
| `journaling-unset` | a required journaling slot has no SQL; `bundle` would refuse | skill `sqetch-new-project` |
| `checks-skipped` | something the rule checks read cannot be trusted yet (a stale index, a plan's release, a duplicate, an unreadable or mismatched document), so they did not run | what the row says: `--fix`, or resolve those findings first |

`--fix` repairs only the findings marked `fixedByReindex` in `-o json`: the index, a `plan.json`'s
release, and the step-folder ones. Fix the rest deliberately and explain each change to the user;
never edit files inside a finalized release.
