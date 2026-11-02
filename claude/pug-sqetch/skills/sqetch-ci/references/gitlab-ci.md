# Sqetch in GitLab CI

Use the `sqetch-deploy` image. GitLab starts a shell inside the job's image, so override the image's
entrypoint and call the tool by name. Pin the image's version.

```yaml
stages: [test, bundle, deploy]

variables:
  SQETCH_DEPLOY_IMAGE: ghcr.io/ndwx/sqetch-deploy:0.1.0

.sqetch-deploy:
  image:
    name: $SQETCH_DEPLOY_IMAGE
    entrypoint: [""]

test-database:
  extends: .sqetch-deploy
  stage: test
  rules:
    - if: $CI_PIPELINE_SOURCE == "merge_request_event"
      changes: [db/**/*]
  services:
    - name: postgres:17
      alias: postgres
  variables:
    POSTGRES_DB: app
    POSTGRES_PASSWORD: postgres
    PGPASSWORD: postgres
  script:
    - sqetch-deploy bundle --project db --test -o test.zip
    - >-
      sqetch-deploy deploy test.zip --driver postgres
      --postgres-host postgres --postgres-database app --postgres-username postgres
      --postgres-password-env PGPASSWORD --rollback on-success

bundle-database:
  extends: .sqetch-deploy
  stage: bundle
  rules:
    - if: $CI_COMMIT_TAG
  script:
    - sqetch-deploy bundle --project db -o db-bundle.zip
  artifacts:
    paths: [db-bundle.zip]

.deploy-database:
  extends: .sqetch-deploy
  stage: deploy
  rules:
    - if: $CI_COMMIT_TAG
  needs: [bundle-database]
  resource_group: $CI_ENVIRONMENT_NAME
  script:
    - >-
      sqetch-deploy deploy db-bundle.zip --driver postgres
      --postgres-host "$DB_HOST" --postgres-database "$DB_NAME" --postgres-username "$DB_USER"
      --postgres-password-env DB_PASSWORD

deploy-staging:
  extends: .deploy-database
  environment: staging

deploy-production:
  extends: .deploy-database
  environment: production
  needs: [bundle-database, deploy-staging]
  when: manual
```

- Define `DB_HOST`, `DB_NAME`, `DB_USER` and a masked, protected `DB_PASSWORD` per environment, in
  *Settings → CI/CD → Variables*, scoped to the environment.
- `resource_group` serializes deployments to one environment's database.
- The image runs as a non-root user (uid 1654). GitLab's Docker executor makes the checkout
  writable by any user by default, so `bundle` can write there. On a runner that turns that off
  (`FF_DISABLE_UMASK_FOR_DOCKER_EXECUTOR`), `bundle` fails with exit code `73`.
- The same image is on Docker Hub as `ndwx/sqetch-deploy`.
