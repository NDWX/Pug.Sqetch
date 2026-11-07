# sqetch-deploy exit codes

The tens digit names the category, the units digit the failure within it. A units digit of `0` is
the category's unspecified code. A one-digit non-zero code is unclassified. A pipeline may branch on
the category alone. The message printed with the code names the cause.

| Code | Meaning | What to do |
|---|---|---|
| `0` | Deployed, nothing to deploy, or deployed and then undone on request; for `status`, it ran (without `--exit-code`, whatever it found) | Nothing. The output tells them apart |
| `1` | Unclassified failure | Read the message |
| `10` | Command-line mistake: unknown command or option, bad value, a switch the driver does not take, an unknown `--bundle-type` | Fix the command; check `--help` for the installed version |
| `21` | Bundle not found | Check the path |
| `22` | Bundle invalid or damaged | Rebuild it |
| `23` | Bundle incompatible with this database's journal: another lineage, a gap before a continuation bundle, a database left at unreleased plans | Deploy the right bundle; never retry as is |
| `24` | Bundle needs a newer `sqetch-deploy` (unknown layout or newer format) | Upgrade the deploying `sqetch-deploy` to at least the bundling one |
| `25` | Since 0.2: the bundle's journaling SQL lacks optional statements the command needs (`status` needs `JournalExists`) | Set them with `sqetch journaling set` and rebuild the bundle |
| `31` | Unknown driver | `sqetch-deploy drivers list` |
| `32` | The driver could not be created: bad or missing connection switches | `sqetch-deploy drivers parameters <driver>` |
| `33` | Database unreachable | Check host, network and credentials; retrying later is safe |
| `34` | Since 0.2: another deployment holds the database's deployment lock (the journaling SQL's `AcquireLock`); nothing was changed | Retry when the other deployment has finished |
| `41` | `PrepareJournal` failed | Fix that journaling SQL, or the role's rights |
| `42` | A journal query failed, or returned something unusable. After `rollback` (since 0.2), most often a database with no journal: the wrong database | Check the database named first; otherwise fix the query the message names: `GetLatestRelease`, `GetDeployedPlans`, or (since 0.2) `JournalExists` or `AcquireLock` |
| `43` | A journal write failed | Fix the failing write slot's SQL |
| `51` | A step's deploy or (since 0.2) verify script failed; the database is as it was before the run | Fix the script (a new plan if its release is finalized), rebuild, deploy again |
| `52` | A step's deploy or verify script failed partway; work committed before it stays | Fix the cause, deploy the same or a rebuilt bundle again: it resumes |
| `61` | A rollback script failed; the database is neither deployed nor undone (after `rollback`: partly undone) | Stop. A person must repair the database. After `sqetch-deploy rollback` (since 0.2), fixing the script and rerunning the same rollback continues it |
| `62` | Since 0.2, `rollback`: the target does not fit what is deployed (a release outside the bundle's lineage, a plan the release lacks, a deployed plan requiring one being undone); nothing ran | Follow the message |
| `71` | `bundle`: the project could not be read (not a project folder, damaged, or written by a newer `sqetch`) | Check `--project`; upgrade `sqetch-deploy` if the project is newer |
| `72` | `bundle`: the project cannot be bundled: unset journaling statements, a missing step script, `--since` naming an unknown release, or nothing to bundle | Follow the message |
| `73` | `bundle`: the output could not be written; it may already exist | Choose another `-o`, or remove the old file |
| `81` | Since 0.2, `status --exit-code` only: the bundle fits and deploying it would deploy something. Not a failure | Deploy, or review first |
| `91` | Not implemented (before 0.2: the `rollback` command) | Use `deploy --rollback`, or upgrade to 0.2 |

Precedence: a failed rollback (`61`) outranks the failure that triggered it, and a database left
partway (`52`) outranks its cause. The cause is still in the message.

Retrying is safe in the sense that a deployment resumes from the journal and never repeats what is
in place. A retry without fixing the cause fails the same way.

The exception is MySQL and MariaDB (driver `mysql`, since 0.2), where schema changes commit as they
run. A failed step there is always `52` unless `--rollback on-error` undid it, and a retry restarts
the failed plan from its first step, which may then fail on what is already there. See the `mysql`
driver in `drivers.md`.
