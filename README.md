# Sqetch

A database schema upgrader for DevOps pipelines, made of two command-line tools:

- **`sqetch`** authors a project: plans of schema change, each made of steps with deploy, verify and
  rollback scripts, grouped into releases.
- **`sqetch-deploy`** bundles a project for a pipeline and deploys bundles to a database (SQLite and
  PostgreSQL), journaling what it deployed through SQL the project defines.

The two tools are versioned and released separately.

## Concepts

A Sqetch **project** lives in your application's repository, beside the code whose schema it
changes. It is plain folders and files, so its history is your repository's: changes are reviewed,
merged and branched like the code. The project also holds its **journaling** SQL, the statements
that record each deployment in a table of your choosing. Set them once with `sqetch journaling`;
`sqetch-deploy bundle` refuses until they are all set.

| Sqetch | What it is | In your development lifecycle |
|---|---|---|
| **Step** | One schema change, with three scripts: `deploy.sql`, `verify.sql` and `rollback.sql` | One reviewable change, about the size of a commit |
| **Plan** | Steps in order; a plan can depend on other plans | A feature, ticket or pull request. It is *unreleased* while you work on it |
| **Release** | Plans shipped together; each release builds on the one before it | A version, sprint or milestone. It is *open* while assembled and tested, and *finalized* when it ships |
| **Bundle** | A deployable snapshot of the project, with the journaling SQL | The build artifact your CI produces once and promotes through environments |
| **Deployment** | A bundle applied to one database and recorded in its journal | The CD stage that runs for each environment |

### Through the lifecycle

1. **Develop.** On your feature branch, create a plan and its steps, write the scripts the
   commands print the paths of, and commit them with the code:

   ```sh
   sqetch plan create -n add-orders
   sqetch plan add-step -p add-orders -n create-orders-table
   ```

   A plan that builds on another names it: `sqetch plan create -n add-customers --require-plans add-orders`.

2. **Test.** In CI, bundle everything, including open releases and unreleased plans, and deploy it
   to a fresh database. `--rollback on-success` proves the deploy scripts apply and the rollback
   scripts undo them:

   ```sh
   sqetch-deploy bundle --test -o test-bundle.zip
   sqetch-deploy deploy test-bundle.zip --driver sqlite --sqlite-file test.db --rollback on-success
   ```

   Use a fresh test database each time. Sqetch never deploys into a release it has already
   completed on a database, so plans added to a release after it was deployed there would not
   reach it.

3. **Release.** Gather the plans into a release, then finalize it when it ships. Finalizing freezes
   the release and its plans, so a fix after that is a new plan in the next release:

   ```sh
   sqetch release create -n 1.4 --all-unreleased        # or --plans add-orders,add-customers
   sqetch release finalize -n 1.4
   sqetch release create -n 1.5 --depends-on 1.4 --all-unreleased
   ```

   Every release after the first names the one it builds on with `--depends-on`.

4. **Deploy.** Bundle the finalized releases, which is what `bundle` does by default, and deploy
   the same bundle to each environment in turn:

   ```sh
   sqetch-deploy bundle -o release-1.4.zip
   sqetch-deploy deploy release-1.4.zip --driver postgres --postgres-connection-string "$CONNECTION_STRING"
   ```

   Each database's journal decides where deployment starts. Releases already there are skipped, a
   deployment that failed partway resumes where it stopped, and deploying an unchanged bundle
   again does nothing. `bundle --since 1.4` makes a bundle holding only the releases after 1.4,
   for databases that already have it.

`verify.sql` is written and bundled with each step, but `sqetch-deploy` does not run it yet.

### Getting help

Both tools describe their own commands. Add `--help` (or `-h`) at any level, from the tool down to a
single command:

```sh
sqetch --help                    # project, plan, release, journaling
sqetch plan --help               # the plan commands
sqetch plan create --help        # one command's options
sqetch-deploy --help             # bundle, deploy, drivers
sqetch-deploy deploy --help
```

A few commands answer questions that `--help` can't:

- `sqetch-deploy drivers list` lists the database drivers, and `sqetch-deploy drivers parameters
  <driver>` shows the options a driver takes, such as `--postgres-host`, and which of them are
  required together.
- `sqetch journaling parameters` lists the parameters each journaling statement can use, and
  `sqetch journaling list` shows which statements the project has set.
- `--version` prints either tool's version.

## Installing

Every release on the [GitHub releases page](https://github.com/NDWX/Pug.Sqetch/releases) carries
the files below. None of them needs .NET except the dotnet tool. `<v>` is the version, for example
`0.1.0`.

| Platform | File | Install |
|---|---|---|
| Linux, macOS | `sqetch-<v>-{linux,osx}-{x64,arm64}.tar.gz` | Extract the folder and put it on your `PATH` |
| Windows | `sqetch-<v>-win-{x64,arm64}.zip` | Extract the folder and put it on your `PATH` |
| Windows | `sqetch-<v>-win-{x64,arm64}.msi` | Run the installer, which adds the tool to `PATH` |
| Debian, Ubuntu | `sqetch_<v>_{amd64,arm64}.deb` | `sudo apt install ./sqetch_<v>_amd64.deb` |
| Fedora, RHEL | `sqetch-<v>.{x86_64,aarch64}.rpm` | `sudo dnf install ./sqetch-<v>.x86_64.rpm` |
| Any, pinned per repository | the archives above, through mise | See [mise](#per-repository-without-net-mise) |
| Containers (`sqetch-deploy`) | `ghcr.io/ndwx/sqetch-deploy:<v>`, Docker Hub `ndwx/sqetch-deploy:<v>` | See [Container image](#container-image-sqetch-deploy) |
| GitHub Actions (`sqetch-deploy`) | the archives above, through the install action | See [GitHub Actions](#github-actions) |
| .NET 10 | nuget.org package `Pug.Sqetch.Cli` | See [As a dotnet tool](#as-a-dotnet-tool) |

`sqetch-deploy` ships the same way, with `sqetch-deploy` in place of `sqetch`. Its dotnet tool
package is `Pug.Sqetch.Deployment.Cli`. Check downloads against the release's `SHA256SUMS`. Pre-release
files carry versions like `0.1.0-pre.202610172239`. Winget, Chocolatey and Homebrew packages are not
published yet.

The archives hold a self-contained single executable. Keep `sqetch-deploy`'s folder together: the
native SQLite library sits beside the executable.

### Per repository, without .NET (mise)

[mise](https://mise.jdx.dev) installs both tools from the release archives and pins their versions
in the repository, so everyone working on the project, and its pipeline, runs the same ones. There
are two ways to set it up: the mise file each release carries, or a configuration you write
yourself.

**From the release's mise file.** This is the simplest way. Every release carries
`<name>-<v>.mise.toml`, which names each platform's archive by its download URL and SHA-256. Save it
in the repository's `.config/mise/conf.d/` folder, commit it, and install:

```sh
mkdir -p .config/mise/conf.d
curl -fsSL -o .config/mise/conf.d/sqetch-deploy.toml \
  https://github.com/NDWX/Pug.Sqetch/releases/download/sqetch_deploy-v0.1.0/sqetch-deploy-0.1.0.mise.toml
mise install
```

On Windows, in PowerShell:

```powershell
New-Item -ItemType Directory -Force .config/mise/conf.d | Out-Null
Invoke-WebRequest -OutFile .config/mise/conf.d/sqetch-deploy.toml `
  https://github.com/NDWX/Pug.Sqetch/releases/download/sqetch_deploy-v0.1.0/sqetch-deploy-0.1.0.mise.toml
mise install
```

For `sqetch`, the release tag is `sqetch-v<v>` and the file `sqetch-<v>.mise.toml`; save it as
`.config/mise/conf.d/sqetch.toml`.

- **Platforms**: one file covers Linux, macOS and Windows on x64 and arm64, so a team on different
  operating systems shares it.
- **Merging**: mise merges the files in `conf.d` with any `mise.toml` the repository already has,
  so neither needs editing.
- **Versions**: to change version, replace the file with another release's and run `mise install`.
  Each tool has its own file, since the two are released separately.
- **Pre-releases**: a pre-release's file works the same way. Its download URL keeps the tag's
  `-pre-v<X.Y.Z>`, while the file name carries the full version, such as
  `sqetch-deploy-0.1.0-pre.202610172239.mise.toml`.
- **Checksums**: mise refuses a download that doesn't match the SHA-256 in the file, and the file
  itself is listed in the release's `SHA256SUMS`. mise makes no GitHub API calls for it, so no
  token is needed.

**By hand.** Write the configuration yourself, for example to keep everything in one `mise.toml`.
Add this at the repository root, or merge it into the one you have:

```toml
[tool_alias]
sqetch = "github:NDWX/Pug.Sqetch"
sqetch-deploy = "github:NDWX/Pug.Sqetch"

[tools.sqetch]
version = "0.1.0"
version_prefix = "sqetch-v"

[tools.sqetch.platforms]
linux-x64 = { asset_pattern = "sqetch-*-linux-x64.tar.gz" }
linux-arm64 = { asset_pattern = "sqetch-*-linux-arm64.tar.gz" }
macos-x64 = { asset_pattern = "sqetch-*-osx-x64.tar.gz" }
macos-arm64 = { asset_pattern = "sqetch-*-osx-arm64.tar.gz" }
windows-x64 = { asset_pattern = "sqetch-*-win-x64.zip" }
windows-arm64 = { asset_pattern = "sqetch-*-win-arm64.zip" }

[tools.sqetch-deploy]
version = "0.1.0"
version_prefix = "sqetch_deploy-v"

[tools.sqetch-deploy.platforms]
linux-x64 = { asset_pattern = "sqetch-deploy-*-linux-x64.tar.gz" }
linux-arm64 = { asset_pattern = "sqetch-deploy-*-linux-arm64.tar.gz" }
macos-x64 = { asset_pattern = "sqetch-deploy-*-osx-x64.tar.gz" }
macos-arm64 = { asset_pattern = "sqetch-deploy-*-osx-arm64.tar.gz" }
windows-x64 = { asset_pattern = "sqetch-deploy-*-win-x64.zip" }
windows-arm64 = { asset_pattern = "sqetch-deploy-*-win-arm64.zip" }
```

Leave out either tool if the repository doesn't need it. Then install the pinned versions, after
cloning and as a pipeline step:

```sh
mise install
```

- **Versions**: change `version` and run `mise install` again. The two tools are released
  separately, so their versions need not match.
- **Pre-releases**: their tags carry no build stamp, so pin the version's `X.Y.Z` and change the
  prefix to `sqetch-pre-v` or `sqetch_deploy-pre-v`.
- **Checksums**: mise checks each download against the SHA-256 that GitHub publishes for the file.
  `mise lock` also records the checksums of every platform's file in `mise.lock`; commit it to have
  a changed file refused.
- **Rate limits**: mise reads the releases through the GitHub API. Set `GITHUB_TOKEN` in pipelines
  and on machines that share an address, since anonymous requests are limited.

The per-platform table is needed because the archive names say `osx` and `win`, and a release
also carries installers that a looser pattern would match.

Either way, with [mise activated](https://mise.jdx.dev/getting-started.html) in your shell, `sqetch`
and `sqetch-deploy` are on `PATH` whenever you are inside the repository, and only there. Without
activation, run them through mise: `mise exec -- sqetch-deploy deploy …`.

### Container image (sqetch-deploy)

Every `sqetch-deploy` release is also a container image for linux/amd64 and linux/arm64, which needs
nothing installed but a container runtime:

```sh
docker run --rm -v "$PWD:/work" --user "$(id -u):$(id -g)" ghcr.io/ndwx/sqetch-deploy:0.1.0 \
  deploy app.zip --driver postgres --postgres-connection-string "$CONNECTION_STRING"
```

The same image is on Docker Hub as `ndwx/sqetch-deploy`. Releases are also tagged `latest`, but pin a
version in a pipeline.

- The entrypoint is `sqetch-deploy`, so the arguments are its own.
- The working directory is `/work`: mount the folder holding the bundle, or the project to bundle,
  there.
- The image runs as a non-root user (`app`, 1654). Passing `--user` with your own ids lets it write
  into the mounted folder (a bundle, a SQLite database) and leaves those files owned by you. A
  deployment to a database server writes nothing locally and needs no `--user`.

In GitLab CI, override the entrypoint so the runner can start its shell, and call the tool by name:

```yaml
deploy:
  image:
    name: ghcr.io/ndwx/sqetch-deploy:0.1.0
    entrypoint: [""]
  script:
    - sqetch-deploy deploy app.zip --driver postgres --postgres-host db --postgres-database app --postgres-password-env DB_PASSWORD
```

On GitHub Actions, the [install action](#github-actions) is simpler than a container job.

### GitHub Actions

The `install-sqetch-deploy` action downloads a `sqetch-deploy` release for the runner (Linux, macOS
or Windows, x64 or arm64), checks it against the release's `SHA256SUMS` and puts it on `PATH` for the
rest of the job:

```yaml
steps:
  - uses: NDWX/Pug.Sqetch/actions/install-sqetch-deploy@sqetch_deploy-v0.1.0
    with:
      version: 0.1.0
  - run: sqetch-deploy deploy app.zip --driver postgres --postgres-connection-string "$CONNECTION_STRING"
```

- `version` is the tool's version as its release files name it, so a pre-release is
  `0.1.0-pre.202610172239`. It is required: a pipeline should not change tools by itself.
- The `@ref` picks the action's own code, separately from `version`. A release tag or a commit SHA
  keeps it fixed.
- The outputs are `path`, the folder holding the executable, and `version`.
- The action needs `bash` and `curl`, plus `tar` on Linux and macOS and Windows PowerShell on
  Windows. GitHub's runners have them all, with Git Bash providing `bash` on Windows. On a
  self-hosted runner, a version is downloaded once and kept in the runner's tool cache.

### As a dotnet tool

With the .NET 10 SDK installed, either tool can be installed from nuget.org.

**Per repository (recommended).** A tool manifest in the repository pins the version, so everyone
working on the project, and its pipeline, runs the same one:

```sh
dotnet new tool-manifest                        # creates dotnet-tools.json; commit it
dotnet tool install Pug.Sqetch.Cli              # recorded in the manifest, not installed globally
dotnet tool install Pug.Sqetch.Deployment.Cli
```

After cloning, and as a pipeline step, restore the pinned versions and run the tools through
`dotnet`:

```sh
dotnet tool restore
dotnet sqetch --help
dotnet sqetch-deploy deploy bundle.zip --driver sqlite --sqlite-file app.db
```

The manifest applies to the folder that holds it and everything below it. The tools are not added
to `PATH`, which is why the commands start with `dotnet`. To move to a newer version, run
`dotnet tool update Pug.Sqetch.Cli` and commit the changed manifest.

**Globally**, on `PATH` as `sqetch` and `sqetch-deploy`:

```sh
dotnet tool install -g Pug.Sqetch.Cli
dotnet tool install -g Pug.Sqetch.Deployment.Cli
```

`dotnet tool install` skips pre-releases. Add `--prerelease` for the newest one, or pin one with
`--version 0.1.0-pre.202610172239`.

## Licence

The command-line tools and packaging are GPL-3.0-or-later; everything else is AGPL-3.0-or-later.
See [LICENSING.md](LICENSING.md).
