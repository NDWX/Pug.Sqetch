# Sqetch in other CI systems

Azure Pipelines, Jenkins, Bitbucket Pipelines, CircleCI, Buildkite and the rest need the same three
stages as in `SKILL.md`. Only how `sqetch-deploy` is installed differs. In order of preference:

## 1. The container image

Where jobs run in containers (Bitbucket, CircleCI's Docker executor, Kubernetes agents, Azure
Pipelines container jobs), use `ghcr.io/ndwx/sqetch-deploy:<v>` (or Docker Hub's
`ndwx/sqetch-deploy:<v>`) as the job image. Override the entrypoint if the system runs a shell in it,
as GitLab does.

Where jobs run on a host with Docker, call it per command:

```sh
docker run --rm -v "$PWD:/work" --user "$(id -u):$(id -g)" ghcr.io/ndwx/sqetch-deploy:0.1.0 \
  bundle --project db -o db-bundle.zip
docker run --rm -v "$PWD:/work" -e DB_PASSWORD ghcr.io/ndwx/sqetch-deploy:0.1.0 \
  deploy db-bundle.zip --driver postgres --postgres-host "$DB_HOST" --postgres-database "$DB_NAME" \
  --postgres-username "$DB_USER" --postgres-password-env DB_PASSWORD
```

`-e DB_PASSWORD` passes the variable into the container without writing its value on the command
line.

## 2. mise

If the repository pins the tools with mise, install mise on the agent (see https://mise.jdx.dev),
then:

```sh
mise install
mise exec -- sqetch-deploy --version
```

## 3. The release archive

On any Linux or macOS agent with `curl`, `tar` and `sha256sum` (macOS: `shasum -a 256`):

```sh
v=0.1.0
rid=linux-x64            # linux-arm64, osx-x64, osx-arm64
base=https://github.com/NDWX/Pug.Sqetch/releases/download/sqetch_deploy-v$v
curl -fsSLO "$base/sqetch-deploy-$v-$rid.tar.gz"
curl -fsSLO "$base/SHA256SUMS"
sha256sum --check --ignore-missing SHA256SUMS
tar -xzf "sqetch-deploy-$v-$rid.tar.gz"
export PATH="$PWD/sqetch-deploy-$v:$PATH"
sqetch-deploy --version
```

On Windows agents, the `win-x64` / `win-arm64` zip holds the same folder; check it against
`SHA256SUMS` with `Get-FileHash`.

## Everywhere

- Build the bundle once and pass it between stages as an artifact.
- Serialize deployments to each database (Azure Pipelines environments with exclusive locks,
  Jenkins `lock`, Buildkite concurrency groups).
- Pass the password through an environment variable and `--postgres-password-env`.
