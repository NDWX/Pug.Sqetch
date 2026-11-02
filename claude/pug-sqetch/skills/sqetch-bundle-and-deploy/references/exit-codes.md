# sqetch-deploy exit codes

The tens digit names the category, the units digit the failure within it. A units digit of `0` is
the category's unspecified code. A one-digit non-zero code is unclassified. A pipeline may branch on
the category alone. The message printed with the code names the cause.

| Code | Meaning | What to do |
|---|---|---|
| `0` | Deployed, nothing to deploy, or deployed and then undone on request | Nothing. The output tells the three apart |
| `1` | Unclassified failure | Read the message |
| `10` | Command-line mistake: unknown command or option, bad value, a switch the driver does not take, an unknown `--bundle-type` | Fix the command; check `--help` for the installed version |
| `21` | Bundle not found | Check the path |
| `22` | Bundle invalid or damaged | Rebuild it |
| `23` | Bundle incompatible with this database's journal: another lineage, a gap before a continuation bundle, a database left at unreleased plans | Deploy the right bundle; never retry as is |
| `24` | Bundle needs a newer `sqetch-deploy` (unknown layout or newer format) | Upgrade the deploying `sqetch-deploy` to at least the bundling one |
| `31` | Unknown driver | `sqetch-deploy drivers list` |
| `32` | The driver could not be created: bad or missing connection switches | `sqetch-deploy drivers parameters <driver>` |
| `33` | Database unreachable | Check host, network and credentials; retrying later is safe |
| `41` | `PrepareJournal` failed | Fix that journaling SQL, or the role's rights |
| `42` | A journal query failed, or returned something unusable | Fix `GetLatestRelease` / `GetDeployedPlans` |
| `43` | A journal write failed | Fix the failing write slot's SQL |
| `51` | A step script failed; the database is as it was before the run | Fix the script (a new plan if its release is finalized), rebuild, deploy again |
| `52` | A step script failed partway; work committed before it stays | Fix the cause, deploy the same or a rebuilt bundle again: it resumes |
| `61` | A rollback script failed; the database is neither deployed nor undone | Stop. A person must repair the database |
| `71` | `bundle`: the project could not be read (not a project folder, damaged, or written by a newer `sqetch`) | Check `--project`; upgrade `sqetch-deploy` if the project is newer |
| `72` | `bundle`: the project cannot be bundled: unset journaling statements, a missing step script, `--since` naming an unknown release, or nothing to bundle | Follow the message |
| `73` | `bundle`: the output could not be written; it may already exist | Choose another `-o`, or remove the old file |
| `91` | Not implemented (the `rollback` command) | Use `deploy --rollback` |

Precedence: a failed rollback (`61`) outranks the failure that triggered it, and a database left
partway (`52`) outranks its cause. The cause is still in the message.

Retrying is safe in the sense that a deployment resumes from the journal and never repeats what is
in place. A retry without fixing the cause fails the same way.
