# Releasing

The two CLIs, `sqetch` and `sqetch-deploy`, are versioned and released independently. Each
carries its own `<Version>` in its project (`Pug.Sqetch.Cli`, `Pug.Sqetch.Deployment.Cli`), and a
release is started by pushing a tag. The GitHub workflow builds every artifact, smoke-tests each one
on its native platform, and attaches all of them to a **draft** GitHub release, which you publish
yourself.

Pushing to package managers (nuget.org, winget, Chocolatey, the Homebrew tap) is **not automated
yet**. The release already carries every file those channels need.

## Tags

| Tag | Builds | Signing |
|---|---|---|
| `sqetch-v1.2.3` / `sqetch_deploy-v1.2.3` | version `1.2.3` | **required** (Windows Authenticode) |
| `sqetch-pre-v1.2.3` / `sqetch_deploy-pre-v1.2.3` | version `1.2.3-pre.<yyyyMMddHHmm>` (UTC build time) | skipped |

- The tag's `X.Y.Z` must equal the project's `<Version>`; the workflow fails first thing otherwise.
  Bump `<Version>` in a commit, then tag that commit.
- A release tag fails in its first job when the signing variables (below) are not configured. Until
  they are, use pre-release tags.
- A pre-release tag produces a draft marked *pre-release*. Tagging the same `X.Y.Z` again with
  `-pre-` gives a newer stamp, so pre-releases sort in build order and before the final `X.Y.Z`.
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
| Direct download, winget | `<name>-<v>-win-{x64,arm64}.msi` (per-user or per-machine; signed on release tags) |
| Chocolatey | `<name>.<v>.nupkg`, which embeds the x64 MSI |
| dotnet tool | `Pug.Sqetch.Cli.<v>.nupkg` / `Pug.Sqetch.Deployment.Cli.<v>.nupkg` (framework-dependent, needs .NET 10) |
| apt / dnf | `<name>_<v>_{amd64,arm64}.deb`, `<name>-<v>.{x86_64,aarch64}.rpm` |
| Verification | `SHA256SUMS` |

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
```

Without `-Version`, a stage builds `X.Y.Z-dev.<stamp>`. When you run the stages one after another,
pass the same `-Version` to each. Output goes to `artifacts/`. The MSI and Chocolatey packages build
only on Windows; `.deb`/`.rpm` need Docker.

## Docker Hub login (one-time, before CI can pass)

The test job pulls the PostgreSQL and Ryuk images for the Testcontainers tests. Docker Hub caps
anonymous pulls per IP address, and GitHub-hosted runners share addresses, so CI logs in and its
pulls count against an account instead. In *Settings → Secrets and variables → Actions*:

- the repository **variable** `DOCKERHUB_USERNAME`;
- the repository **secret** `DOCKERHUB_TOKEN`, a Docker Hub personal access token (read-only is
  enough for public images).

Without them the login step fails with "Password required", in `ci.yml` and in the release
workflow's `test` job, which calls it with `secrets: inherit`.

## Signing setup (one-time, before the first release tag)

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

- nuget.org push of the tool packages (trusted publishing).
- winget manifests (`Pug.Sqetch`, `Pug.SqetchDeploy`) pointing at the release's MSIs.
- `choco push` of the Chocolatey packages.
- The Homebrew formulae in `NDWX/homebrew-tap`.
- macOS Developer ID signing and notarization. The macOS binaries are ad-hoc signed only.
- Signing Linux packages and repositories.
