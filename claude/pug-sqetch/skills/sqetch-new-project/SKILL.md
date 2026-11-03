---
name: sqetch-new-project
description: Use when adding Sqetch to a repository or starting a new Sqetch project, i.e. choosing the project folder, setting the user identity (sqetch project user), running sqetch project init, setting the fifteen journaling statements (sqetch journaling init, repository and user templates, and ready SQLite and PostgreSQL SQL for older versions), creating the first plan and step, and proving the project bundles and deploys. Also for a project whose bundle fails because journaling statements are missing.
---

# Starting a Sqetch project

Needs `sqetch` and `sqetch-deploy` installed (skill `sqetch-install`). Check the versions first
(skill `sqetch`, *Versions*).

## 1. Choose the folder

The project lives in the application's repository, in its own folder, for example `db/`. Its
history is the repository's: plans are committed, reviewed and merged with the code they serve.
One project per database schema. Ask the user if the repository has more than one database.

## 2. Set the user identity

Every `sqetch` command records who acted, so it needs an identity. Ask the user for their name and
e-mail address; never invent them.

```sh
mkdir db && cd db
sqetch project user "Ada Lovelace" ada@example.com             # this project only, in ./.sqetch-user
sqetch project user "Ada Lovelace" ada@example.com --global    # every project, in ~/.sqetch-user
```

`sqetch` looks in the project folder first, then the home folder. The project's `.sqetch-user` is
personal: add `.sqetch-user` to the repository's `.gitignore`. `sqetch-deploy` needs no identity,
so a pipeline never needs one.

## 3. Initialize

In the project folder:

```sh
sqetch project init orders-db -d "Schema of the orders service" --engine postgres
```

- `<name>` names the project; it is recorded in every bundle and passed to journaling as
  `@project`, so pick it once. Names allow letters, digits and `-_+()@#.`, must start with a
  letter or digit, must not end with `.`, and are at most 128 characters.
- `--engine` names the database engine. Give a `sqetch-deploy` driver name (`sqlite`, `postgres`)
  so `journaling init` can default to it; since 0.2 `init` warns when the name is no driver. It is
  otherwise a label: Sqetch never picks a driver from it at deploy time.
- `--shard-by-prefix [DELIMITER]` keeps finalized releases in folders by the part of their name
  before the delimiter (default `.`), so `1.4.0` is at `releases/1/1.4.0`. It helps a project with
  many version-named releases. Decide now; the default is flat.

`init` creates `sqetch-project.json`, `plan-index`, `plans/` and `releases/`; `journaling/` appears
with the first journaling statement set.

## 4. Set the journaling statements

`sqetch-deploy` records every deployment through SQL the project supplies, in fifteen slots, and
reads it back to decide where a database stands. `bundle` refuses until all fifteen are set
(exit code `72`, naming the missing ones).

Start from a template, adapted only if the user asks.

**Since `sqetch` 0.2**, one command sets all fifteen from the driver's template:

```sh
sqetch journaling init                # the driver named by the project's --engine
sqetch journaling init postgres       # or name it; warns if the engine names another driver
sqetch journaling templates           # what is available, and where from
```

- It refuses when any slot is already set; `--force` replaces them all.
- A team can keep its own templates in the repository, and a person in their user configuration
  folder. Both win over the driver's built-in one, the repository first:
  - repository: `.config/sqetch/` in the project folder or a parent, up to the git root;
  - user: `~/.config/sqetch/` on Linux, `~/Library/Application Support/sqetch/` on macOS,
    `%APPDATA%\sqetch\` on Windows.

  Below either, a template is the folder
  `database-drivers/<driver>/journaling-templates/<name>/` holding all fifteen files, named as in
  a project's `journaling/` folder; copying a tuned project's `journaling/` folder there makes one.
  `default` is used unless `--template <name>` picks another.

**Before 0.2**, set each slot from the references, which hold the same SQL as the built-in
templates:
- SQLite: `references/journaling-sqlite.md`
- PostgreSQL: `references/journaling-postgresql.md`

Write each slot's SQL to a file and set it; the reference shows the loop. Use `--file` or
`--stdin`, not the SQL as an argument: SQL starting with `--` would be read as an option. Write
the files to a temporary folder outside the project, and delete it afterwards.

The contract the templates implement, for writing or changing one, is
`references/journaling-contract.md`. Then check:

```sh
sqetch journaling list                # all fifteen should be set
sqetch journaling print GetLatestRelease
```

`sqetch` keeps its own copy of the statements in the project's `journaling/` folder, which is what
gets committed. Never write into `journaling/` by hand; change a statement with `journaling set`.

## 5. Add a first plan and step

```sh
sqetch plan create -n create-orders -d "Orders table"
sqetch plan add-step -p create-orders -n orders-table
```

`add-step` prints the paths of `deploy.sql`, `verify.sql` and `rollback.sql`. Write them (skill
`sqetch-plans-and-releases` explains how).

## 6. Prove it

Bundle everything, unreleased plans included, and deploy to a throwaway SQLite database, rolling
back afterwards. This proves the journaling SQL, the deploy scripts and the rollback scripts:

```sh
sqetch-deploy bundle --project . --test -o /tmp/proof.zip
sqetch-deploy deploy /tmp/proof.zip --driver sqlite --sqlite-file /tmp/proof.db --rollback on-success
rm /tmp/proof.zip /tmp/proof.db
```

For a PostgreSQL project, deploy to a throwaway PostgreSQL database instead: SQLite will not run
PostgreSQL SQL. Never point `--rollback on-success` at a database that matters.

## 7. Commit

Commit the project folder: `sqetch-project.json`, `plan-index`, `plans/`, `releases/`,
`journaling/`, and the `.gitignore` entry for `.sqetch-user`. Do not commit bundles or test
databases.
