# Sqetch in GitHub Actions

A complete workflow for a PostgreSQL project in `db/`: test on pull requests, bundle on a release
tag, deploy to staging and then production. Adapt the paths, tag pattern, versions and environment
names to the repository; keep the shape.

```yaml
name: database

on:
  pull_request:
    paths: ['db/**']
  push:
    tags: ['v*']

permissions:
  contents: read

jobs:
  test:
    if: github.event_name == 'pull_request'
    runs-on: ubuntu-latest
    services:
      postgres:
        image: postgres:17
        env:
          POSTGRES_PASSWORD: postgres
          POSTGRES_DB: app
        ports: ['5432:5432']
        options: >-
          --health-cmd pg_isready --health-interval 5s --health-timeout 5s --health-retries 10
    steps:
      - uses: actions/checkout@v7
      - uses: NDWX/Pug.Sqetch/actions/install-sqetch-deploy@sqetch_deploy-v0.1.0
        with:
          version: 0.1.0
      - run: sqetch-deploy bundle --project db --test -o test.zip
      - run: >-
          sqetch-deploy deploy test.zip --driver postgres
          --postgres-host localhost --postgres-database app --postgres-username postgres
          --postgres-password-env PGPASSWORD --rollback on-success
        env:
          PGPASSWORD: postgres

  bundle:
    if: github.event_name == 'push'
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
      - uses: NDWX/Pug.Sqetch/actions/install-sqetch-deploy@sqetch_deploy-v0.1.0
        with:
          version: 0.1.0
      - run: sqetch-deploy bundle --project db -o db-bundle.zip
      - uses: actions/upload-artifact@v7
        with:
          name: db-bundle
          path: db-bundle.zip

  deploy-staging:
    needs: bundle
    uses: ./.github/workflows/database-deploy.yml
    with:
      environment: staging
    secrets: inherit

  deploy-production:
    needs: deploy-staging
    uses: ./.github/workflows/database-deploy.yml
    with:
      environment: production
    secrets: inherit
```

The deployment itself, in `.github/workflows/database-deploy.yml`, is written once and called per
environment:

```yaml
name: database-deploy

on:
  workflow_call:
    inputs:
      environment:
        type: string
        required: true

jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: ${{ inputs.environment }}
    concurrency:
      group: database-${{ inputs.environment }}
      cancel-in-progress: false
    steps:
      - uses: actions/download-artifact@v8
        with:
          name: db-bundle
      - uses: NDWX/Pug.Sqetch/actions/install-sqetch-deploy@sqetch_deploy-v0.1.0
        with:
          version: 0.1.0
      - run: >-
          sqetch-deploy deploy db-bundle.zip --driver postgres
          --postgres-host "$DB_HOST" --postgres-database "$DB_NAME" --postgres-username "$DB_USER"
          --postgres-password-env DB_PASSWORD
        env:
          DB_HOST: ${{ vars.DB_HOST }}
          DB_NAME: ${{ vars.DB_NAME }}
          DB_USER: ${{ vars.DB_USER }}
          DB_PASSWORD: ${{ secrets.DB_PASSWORD }}
```

- **Environments** (`staging`, `production`) hold each database's variables and secrets, and
  production's required reviewers. Production runs only after staging succeeded, and
  `concurrency` stops two deployments to one database overlapping.
- **The artifact** uploaded by `bundle` is visible to the called workflow, since it runs in the
  same workflow run.
- **A SQLite project** needs no service: deploy the test bundle with
  `--driver sqlite --sqlite-file test.db --rollback on-success`.
- **A SQL Server project** (driver since 0.2) swaps the service and the deploy step. The image
  starts with no application database, so create one first:

  ```yaml
      services:
        sqlserver:
          image: mcr.microsoft.com/mssql/server:2022-latest
          env:
            ACCEPT_EULA: 'Y'
            MSSQL_SA_PASSWORD: Test-Passw0rd
          ports: ['1433:1433']
          options: >-
            --health-cmd "/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P Test-Passw0rd -Q 'select 1'"
            --health-interval 5s --health-timeout 5s --health-retries 20
      steps:
        # … checkout, install and bundle as above …
        - run: >-
            docker exec ${{ job.services.sqlserver.id }} /opt/mssql-tools18/bin/sqlcmd -C -S localhost
            -U sa -P Test-Passw0rd -Q 'create database app'
        - run: >-
            sqetch-deploy deploy test.zip --driver sqlserver
            --sqlserver-host localhost --sqlserver-database app --sqlserver-username sa
            --sqlserver-password-env SA_PASSWORD --sqlserver-trust-server-certificate true
            --rollback on-success
          env:
            SA_PASSWORD: Test-Passw0rd
  ```

  The image runs on x64 runners only.
- **A MySQL or MariaDB project** (driver since 0.2) uses `mysql:8.4` or `mariadb:11` as the
  service, which creates the database itself:

  ```yaml
      services:
        mysql:
          image: mysql:8.4
          env:
            MYSQL_ROOT_PASSWORD: test
            MYSQL_DATABASE: app
          ports: ['3306:3306']
          options: >-
            --health-cmd "mysqladmin ping -h 127.0.0.1 -ptest"
            --health-interval 5s --health-timeout 5s --health-retries 20
      steps:
        # … checkout, install and bundle as above …
        - run: >-
            sqetch-deploy deploy test.zip --driver mysql
            --mysql-host 127.0.0.1 --mysql-database app --mysql-username root
            --mysql-password-env MYSQL_PASSWORD --rollback on-success
          env:
            MYSQL_PASSWORD: test
  ```

  For MariaDB, use `MARIADB_ROOT_PASSWORD`/`MARIADB_DATABASE` and
  `healthcheck.sh --connect --innodb_initialized` as the health command.
- **The action's `@ref`** pins the action's code and `version` the tool. Bump both together when
  asked to upgrade.

## Installing another way

If the repository pins the tools with mise, install from its own configuration instead of the action:

```yaml
      - uses: jdx/mise-action@v5
      - run: sqetch-deploy --version
```

The release's mise file needs no token. A hand-written mise configuration reads the GitHub API, so
give it `GITHUB_TOKEN: ${{ github.token }}` in `env`.

If it pins them in a dotnet tool manifest:

```yaml
      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: '10.0.x'
      - run: dotnet tool restore
      - run: dotnet sqetch-deploy --version
```
