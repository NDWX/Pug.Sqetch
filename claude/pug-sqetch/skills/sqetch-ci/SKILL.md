---
name: sqetch-ci
description: Use when putting a Sqetch project into a CI/CD pipeline (GitHub Actions, GitLab CI, Azure Pipelines, Jenkins or any other), i.e. testing schema changes on pull requests against a throwaway database, building the release bundle once as an artifact, deploying it to each environment with approvals, installing sqetch-deploy in the pipeline, passing database credentials, and acting on sqetch-deploy's exit codes.
---

# Sqetch in CI/CD

A pipeline needs only **`sqetch-deploy`**: `bundle` reads the project straight from the checkout,
and neither command needs a user identity. Authoring with `sqetch` happens on developers' machines.

Pin `sqetch-deploy` to the version the repository already pins (skill `sqetch`, *Versions*), and keep
it at least as new as the `sqetch` the team uses.

## The pipeline shape

1. **Pull requests: prove the change.** Bundle everything, unreleased plans included, and deploy it
   to a throwaway database, rolling back afterwards. That proves the deploy scripts apply, the
   rollback scripts undo them, and the journaling SQL works:

   ```sh
   sqetch-deploy bundle --project db --test -o test.zip
   sqetch-deploy deploy test.zip --driver postgres --postgres-host localhost \
     --postgres-database app --postgres-username postgres --postgres-password-env PGPASSWORD \
     --rollback on-success
   ```

   Use a fresh database every run (a service container, or a SQLite file for a SQLite project).
   Test databases are single-use; never point this at a shared database.

2. **Release: build the bundle once.** When the application's version ships, its release is
   finalized in the project (skill `sqetch-plans-and-releases`), and the pipeline builds the bundle
   from that commit and keeps it as an artifact:

   ```sh
   sqetch-deploy bundle --project db -o db-bundle.zip
   ```

   Run this on the release tag or the release branch, not on every push. Before any release is
   finalized there is nothing to bundle, and `bundle` fails with `72`.

3. **Deploy: promote the same file.** Each environment's job downloads that artifact and deploys
   it, in order, behind the environment's approvals:

   ```sh
   sqetch-deploy deploy db-bundle.zip --driver postgres --postgres-host "$DB_HOST" \
     --postgres-database "$DB_NAME" --postgres-username "$DB_USER" --postgres-password-env DB_PASSWORD \
     --log deploy.log
   ```

   Since 0.2, run `status` first, with the same bundle, `--driver` and driver switches (not
   `deploy`'s own options such as `--log`), to show what the deploy will do, for the
   approver to read; it changes nothing (skill `sqetch-bundle-and-deploy`). With `--exit-code` it
   exits `81` when something would deploy, `0` when not, so the job can skip the deploy and its
   approval when there is nothing to do:

   ```sh
   set +e
   sqetch-deploy status db-bundle.zip --driver postgres --postgres-host "$DB_HOST" \
     --postgres-database "$DB_NAME" --postgres-username "$DB_USER" --postgres-password-env DB_PASSWORD \
     --exit-code
   status=$?
   set -e
   [ "$status" -eq 0 ] || [ "$status" -eq 81 ] || exit "$status"
   ```

   It needs the project's `JournalExists` journaling statement (exit `25` without it).

   Each database's journal decides what is new, so redeploying is safe, and a failed run resumes.
   Never let two deployments to the same database run at once: serialize the jobs per
   environment.

## Secrets

- Keep passwords in the CI system's secrets and pass them through an environment variable with
  `--postgres-password-env <VARIABLE>`. A password or connection string as an argument is visible
  to other processes and may appear in logs.
- The deploying role needs the rights the step scripts use, and the journal table's.

## Exit codes

Any non-zero code fails the job, which is usually right. When a pipeline should react differently
(skill `sqetch-bundle-and-deploy`, `references/exit-codes.md`):
- `33`, database unreachable: safe to retry later;
- `51` or `52`, a step failed: fix the script, rebuild, and deploy again; it resumes;
- `23` or `24`, a bundle that doesn't fit this database, or needs a newer `sqetch-deploy`: never
  retry as is;
- `61`, a rollback that failed: stop and alert a person.

## Per CI system

| CI | Install `sqetch-deploy` with | Details |
|---|---|---|
| GitHub Actions | the `install-sqetch-deploy` action, or mise / `dotnet tool restore` if the repository pins it that way | `references/github-actions.md` |
| GitLab CI | the container image, entrypoint overridden | `references/gitlab-ci.md` |
| Others (Azure Pipelines, Jenkins, Bitbucket, CircleCI, …) | the container image, mise, or the release archive | `references/other-ci.md` |
