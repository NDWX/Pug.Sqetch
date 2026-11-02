# Releasing

The two CLIs, `sqetch` and `sqetch-deploy`, are versioned and released independently. Each
carries its own `<Version>` in its project (`Pug.Sqetch.Planning.Cli`, `Pug.Sqetch.Deployment.Cli`), and a
release is started by pushing a tag. The GitHub workflow builds every artifact, smoke-tests each one
on its native platform, pushes the dotnet tool package to **nuget.org** and, for `sqetch-deploy`, the
container image to **GHCR** and **Docker Hub**, and attaches every artifact to a **draft** GitHub
release, which you publish yourself.

Pushing to the other package managers (winget, Chocolatey, the Homebrew tap) is **not automated
yet**. The release already carries every file those channels need.

## Tags

| Tag | Builds | Signing |
|---|---|---|
| `sqetch-v1.2.3` / `sqetch_deploy-v1.2.3` | version `1.2.3` | Windows Authenticode when configured, otherwise unsigned |
| `sqetch-pre-v1.2.3` / `sqetch_deploy-pre-v1.2.3` | version `1.2.3-pre.<yyyyMMddHHmm>` (UTC build time) | skipped |

- The tag's `X.Y.Z` must equal the project's `<Version>`; the workflow fails first thing otherwise.
  Bump `<Version>` in a commit, then tag that commit.
- A release tag is signed when every signing variable (below) is set, and builds unsigned when none
  is: the run shows a warning annotation and the draft's notes say the Windows files are unsigned.
  Setting only some of them fails the first job, since that is a broken configuration, not a choice.
- A pre-release tag produces a draft marked *pre-release*. Tagging the same `X.Y.Z` again with
  `-pre-` gives a newer stamp, so pre-releases sort in build order and before the final `X.Y.Z`.
- **Both kinds push the tool package to nuget.org**, a pre-release tag as a NuGet pre-release
  (`dotnet tool install --prerelease`). The push waits for every test and smoke job, runs in the
  `release` environment, and cannot be undone: nuget.org only unlists a version. Any tag fails in its
  first job when `NUGET_USER` (below) is not configured.
- **`sqetch_deploy` tags of both kinds also push the container image**, after the same jobs and in
  the same environment as the nuget.org push, and only then is the draft created. A pushed tag can
  be overwritten, unlike a nuget.org version, but whoever pulled it keeps what they pulled. Any
  `sqetch_deploy` tag fails in its first job when `DOCKERHUB_IMAGE` (below) is not configured.
- `sqetch_deploy` uses an underscore so that no `sqetch-*` tag pattern can match it.

## Dry run

*Actions → release → Run workflow* with a product builds and smoke-tests everything as
`X.Y.Z-dev.<stamp>`, unsigned. The files are kept as workflow artifacts, and no release is created.
It stays unsigned and unreleased even when dispatched from a tag. Use it before tagging.

## What a release contains

For each product, with `<v>` the full version:

| Channel | File |
|---|---|
| Direct download, Homebrew | `<name>-<v>-{linux,osx}-{x64,arm64}.tar.gz` |
| Direct download | `<name>-<v>-win-{x64,arm64}.zip` |
| Direct download, winget | `<name>-<v>-win-{x64,arm64}.msi` (per-user or per-machine; signed on release tags when signing is configured) |
| Chocolatey | `<name>.<v>.nupkg`, which embeds the x64 MSI |
| dotnet tool, also pushed to nuget.org | `Pug.Sqetch.Cli.<v>.nupkg` / `Pug.Sqetch.Deployment.Cli.<v>.nupkg` (framework-dependent, needs .NET 10) |
| apt / dnf | `<name>_<v>_{amd64,arm64}.deb`, `<name>-<v>.{x86_64,aarch64}.rpm` |
| mise | `<name>-<v>.mise.toml`: every platform's archive by its release URL and SHA-256 |
| Verification | `SHA256SUMS` |

`sqetch-deploy` is also a container image for linux/amd64 and linux/arm64, pushed rather than
attached: `ghcr.io/<owner>/sqetch-deploy:<v>` and `docker.io/<DOCKERHUB_IMAGE>:<v>`, and `:latest` as
well for a release (not a pre-release). It is `Installers/docker/Dockerfile` over the two Linux
archives, so it holds exactly the files they do, on the .NET `runtime-deps` base, running as that
image's non-root `app` user (1654).

File names always carry the full version as written (`0.1.0-pre.202610051000`). Inside the
packages, deb and rpm spell a pre-release `0.1.0~pre.…`, so the package manager orders it before
`0.1.0`. The MSI's ProductVersion is the bare `X.Y.Z`, so the final release upgrades over an installed
pre-release of the same version.

The executables are self-contained single files (`sqetch-deploy` keeps the native SQLite library
beside it). Every artifact carries both licence texts, `LICENSING.md`, `THIRD-PARTY-NOTICES.txt` and,
where the .NET runtime is embedded, the runtime's own licence and notices.

## Building locally

All logic lives in `Installers/build/build.ps1` (PowerShell 7). The workflows only call it, so the
same stages run on a workstation:

```sh
pwsh Installers/build/build.ps1 -Stage identify -Ref refs/tags/sqetch-pre-v0.1.0
pwsh Installers/build/build.ps1 -Stage publish -Product sqetch-deploy -Rid linux-x64
pwsh Installers/build/build.ps1 -Stage package -Product sqetch-deploy -Rid linux-x64 -DockerViaSg
pwsh Installers/build/build.ps1 -Stage smoke   -Product sqetch-deploy -DockerViaSg
pwsh Installers/build/build.ps1 -Stage mise-config -Product sqetch-deploy -Version 0.1.0-pre.209911010000
pwsh Installers/build/build.ps1 -Stage smoke-image -Product sqetch-deploy -DockerViaSg
pwsh Installers/build/build.ps1 -Stage push-image  -Product sqetch-deploy -Image ghcr.io/ndwx/sqetch-deploy -DryRun
```

Without `-Version`, a stage builds `X.Y.Z-dev.<stamp>`. When you run the stages one after another,
pass the same `-Version` to each. Output goes to `artifacts/`. The MSI and Chocolatey packages build
only on Windows; `.deb`/`.rpm` need Docker. `identify` on a tag needs `NUGET_USER` set to anything,
as it does in the workflow (and, for `sqetch_deploy`, `DOCKERHUB_IMAGE`). `-Stage push-tool -DryRun`
prints the nuget.org push without making it, and `-Stage push-image -DryRun` the image push. The
image stages read the `linux-x64` archive (`smoke-image`) or both Linux archives (`push-image`) from
`dist/`, so run `package` for those RIDs first. `mise-config` needs all six archives, which only the
workflow's `release` job has together; locally, `package` builds the osx and win archives on Linux
too. It refuses a `-dev` version, which has no release to download from.

## Docker Hub login (one-time, before CI can pass)

The test job pulls the PostgreSQL and Ryuk images for the Testcontainers tests. Docker Hub caps
anonymous pulls per IP address, and GitHub-hosted runners share addresses, so CI logs in and its
pulls count against an account instead. In *Settings → Secrets and variables → Actions*:

- the repository **variable** `DOCKERHUB_USERNAME`;
- the repository **secret** `DOCKERHUB_TOKEN`, a Docker Hub personal access token (read-only is
  enough for public images).

Without them the login step fails with "Password required", in `ci.yml` and in the release
workflow's `test` job, which calls it with `secrets: inherit`.

## After publishing

Publishing a release runs `.github/workflows/verify-release.yml`, which installs it the ways the
README documents, on Linux, macOS and Windows, x64 and arm64:
- through mise, with the release's own `<name>-<v>.mise.toml`, and with the README's hand-written
  snippet;
- for `sqetch-deploy`, through the `install-sqetch-deploy` action;
- for `sqetch-deploy`, by running the image pulled anonymously from both registries.

A draft cannot be tested this way, so this is the first check of the real download path. Its image
job fails until the GHCR package is public (below). Re-run it by hand with *Actions → Verify release →
Run workflow* and the tag.

Then move `main` to the released commit (merge or fast-forward it), and push it. `main` is what the
`pug-claude-plugins` marketplace (`NDWX/claude-code-plugins`) installs the agent skills from, so
this is the step that ships them to users. If the skills changed since the last release, check that
`claude/pug-sqetch/.claude-plugin/plugin.json`'s `version` was bumped; otherwise Claude Code does
not update installed copies.

## Container registries (one-time, before the first `sqetch_deploy` tag)

The `image` job pushes to two registries.

- **GHCR** needs nothing set up: the job's own token may push to `ghcr.io/<owner>/sqetch-deploy`.
  The first push creates the package **private**; make it public once, under *the package's
  settings → Change visibility*. The image's `org.opencontainers.image.source` label links it to
  this repository.
- **Docker Hub** needs:
  - the repository **variable** `DOCKERHUB_IMAGE`, the image's repository without a tag, for example
    `ndwx/sqetch-deploy`;
  - a **secret** `DOCKERHUB_PUSH_TOKEN` in the `release` environment: a personal access token of
    the `DOCKERHUB_USERNAME` account with *Read & Write* access. The `DOCKERHUB_TOKEN` used for pulls
    (above) stays read-only.

## NuGet trusted publishing (one-time, before the first tag)

The `nuget` job logs in to nuget.org with GitHub OIDC (`NuGet/login`), which issues an API key valid
for an hour, so no NuGet key is stored in GitHub.

1. On nuget.org, under *your profile → Trusted Publishing*, add a GitHub Actions policy:
   - repository owner `NDWX`, repository `Pug.Sqetch`;
   - workflow file `release.yml` (the file name only, without `.github/workflows/`);
   - environment `release`, or leave it empty to accept the job from any environment;
   - scopes allowing both new packages and new versions, for `Pug.Sqetch.Cli` and
     `Pug.Sqetch.Deployment.Cli` (or the glob `Pug.Sqetch.*`): the first push of each creates it.

   The policy's owner, you or an organization you belong to, owns the packages it publishes.
2. The repository **variable** `NUGET_USER`: the nuget.org profile name (not the e-mail address).
   It must be a repository variable, not an environment-only one, because the first job checks it
   outside any environment.

A re-run pushes with `--skip-duplicate`, so a version already on nuget.org is not an error.

## Signing setup (optional)

Without it, release tags build unsigned Windows executables and MSIs, which SmartScreen may warn
about until they build up reputation. Nothing else depends on it: nuget.org signs the tool package
itself, and the Linux and macOS artifacts do not use this certificate.

Windows executables and MSIs are signed with the .NET `sign` tool (pinned in
`.config/dotnet-tools.json`) against a certificate in Azure Key Vault. The workflow logs in with
GitHub OIDC, so no secret is stored in GitHub.

1. An Authenticode code-signing certificate from a publicly trusted CA, in an Azure Key Vault.
2. An Entra ID app registration with a federated credential for
   `repo:NDWX/Pug.Sqetch:environment:release`.
3. Its service principal gets the *Key Vault Certificate User* and *Key Vault Crypto User* roles on
   the vault.
4. A GitHub environment named `release`, optionally with required reviewers. The Windows build job
   runs in it when signing. Unsigned builds run in an environment named `unsigned`, which GitHub
   creates on first use and which has no protection rules.
5. Repository **variables**. They must not be environment-only, because the first job checks them
   outside any environment:
   - `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` (no subscription is needed: signing uses only the vault);
   - `SIGN_KEY_VAULT_URL`, for example `https://<vault>.vault.azure.net/`;
   - `SIGN_CERTIFICATE_NAME`.

## Not yet automated (distribution)

- winget manifests (`Pug.Sqetch`, `Pug.SqetchDeploy`) pointing at the release's MSIs.
- `choco push` of the Chocolatey packages.
- The Homebrew formulae in `NDWX/homebrew-tap`.
- macOS Developer ID signing and notarization. The macOS binaries are ad-hoc signed only.
- Signing Linux packages and repositories.
