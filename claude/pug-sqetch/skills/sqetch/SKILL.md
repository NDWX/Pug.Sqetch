---
name: sqetch
description: Use when a repository contains a sqetch-project.json, when someone mentions Sqetch, sqetch or sqetch-deploy, or when you need to change a database schema in a project that manages it with Sqetch. Explains what Sqetch is, how its steps, plans and releases map onto tasks and software releases, the rules for working on a project, how to match the installed tool versions, and how to get help from the CLIs. Sends you to the skill for installing, starting a project, day-to-day authoring, bundling and deploying, or CI.
---

# Sqetch

Sqetch manages a database's schema changes as SQL files that live in the application's repository.
It has two command-line tools, versioned and released separately:

- **`sqetch`** authors a project: plans of schema change, each made of steps with `deploy.sql`,
  `verify.sql` and `rollback.sql`, grouped into releases.
- **`sqetch-deploy`** bundles a project into a deployable file and deploys bundles to a database
  (SQLite and PostgreSQL), journaling what it deployed through SQL the project defines.

Source, releases and documentation: https://github.com/NDWX/Pug.Sqetch

## The concepts, mapped to the work

| Sqetch | What it is | In the development lifecycle |
|---|---|---|
| **Step** | One schema change: `deploy.sql`, `verify.sql`, `rollback.sql` | One reviewable change, about the size of a commit |
| **Plan** | Steps in order; may depend on other plans | A task, ticket or pull request. *Unreleased* while worked on |
| **Release** | Plans shipped together; each release builds on the one before | A software version. *Open* while assembled and tested, *finalized* when it ships |
| **Bundle** | A deployable snapshot of the project, with its journaling SQL | The build artifact CI produces once and promotes |
| **Deployment** | A bundle applied to one database, recorded in its journal | The CD stage for each environment |

A **project** is a folder (`sqetch-project.json` at its root) inside the application's repository.
Its **journaling** SQL records each deployment in a table the project chooses, and is what tells
`sqetch-deploy` where each database stands.

## Rules for working on a project

- **`sqetch` acts on the project in the current directory.** `cd` into the folder holding
  `sqetch-project.json` first. Only `sqetch-deploy bundle` takes a `--project <dir>`.
- **Create and move things with commands; edit only the `.sql` files by hand.** Never edit
  `plan.json`, `step.json`, `release.json`, `sqetch-project.json` or `plan-index`, and never move
  plan or release folders yourself: the commands move them as plans join releases and releases are
  finalized.
- **A finalized release is frozen**, with its plans and their scripts. A change after that is a new
  plan in a later release, never an edit.
- **Every `sqetch` command needs a user identity**, set once with `sqetch project user`; only that
  command, `sqetch journaling parameters` and `sqetch journaling templates` work without one. `sqetch-deploy` needs none. Ask the user for their name and e-mail
  address rather than inventing them.
- **Never deploy to a database the user did not name**, and never with `--rollback on-success`
  against anything but a throwaway database.
- **Read output as data with `-o json`** (or `-o csv`) on the list commands. Without `-o`, they
  print a table on a terminal and tab-separated rows when piped.

## Versions

The two tools release separately, and a repository pins the versions it uses. Before running
anything:

1. **Find the pinned versions**: `.config/mise/conf.d/*.toml` or `mise.toml`,
   `dotnet-tools.json` (at the root or in `.config/`), a CI file's `install-sqetch-deploy` `version:` or a
   `sqetch-deploy` image tag.
2. **Check what runs**: `sqetch --version`, `sqetch-deploy --version`. Through a tool manifest they
   are `dotnet sqetch …`, through mise without activation `mise exec -- sqetch …`.
3. **The installed tool's `--help` is the authority.** These skills are written for the latest
   releases (0.1.0) and mark later additions "since X.Y". When a skill and `--help` disagree,
   follow `--help`, and tell the user that the pinned version lacks what the skill describes.
4. **Never upgrade or change a pinned version unless asked.**
5. **Keep `sqetch-deploy` at least as new as `sqetch`.** A project written by a newer `sqetch`
   makes `bundle` fail with exit code `71`, and a bundle built by a newer `sqetch-deploy` makes
   `deploy` fail with `24`. The remedy for both is upgrading `sqetch-deploy`.

## Getting help

Both tools describe themselves. Add `--help` (or `-h`) at any level:

```sh
sqetch --help                    # project, plan, release, journaling
sqetch plan --help               # the plan commands
sqetch plan create --help        # one command's options
sqetch-deploy --help             # bundle, deploy, drivers
sqetch-deploy deploy --help
```

Some answers `--help` cannot give:

- `sqetch-deploy drivers list` lists the database drivers, and
  `sqetch-deploy drivers parameters <driver>` shows a driver's switches (such as `--postgres-host`)
  and which sets of them are required together.
- `sqetch journaling parameters [<slot>]` lists the parameters each journaling statement may use.
  It needs no project.
- `sqetch journaling list` shows which journaling statements the project has set, and
  `sqetch journaling print <slot>` prints one.
- `sqetch journaling templates [<driver>]` (since 0.2) lists the journaling templates
  `sqetch journaling init` can copy into a project: built into each driver, or kept in the
  repository or the user's configuration folder. It needs no project.
- `--version` prints either tool's version.

## Where to go next

| Task | Skill |
|---|---|
| Install the tools on a machine, in a repository or in CI | `sqetch-install` |
| Start a project, including its journaling SQL | `sqetch-new-project` |
| Turn a task into a plan and steps, assemble and finalize releases | `sqetch-plans-and-releases` |
| Build a bundle, deploy it, roll back, read exit codes | `sqetch-bundle-and-deploy` |
| Test, bundle and deploy in a pipeline | `sqetch-ci` |
