---
name: sqetch-install
description: Use when sqetch or sqetch-deploy is not installed, when adding Sqetch to a repository, a developer machine or a CI pipeline, or when choosing between mise, a dotnet tool manifest, the sqetch-deploy container image, the install-sqetch-deploy GitHub Action and the release archives or packages. Covers detecting what a repository already uses, pinning versions, pre-releases and checksums.
---

# Installing Sqetch

`sqetch` (authoring) and `sqetch-deploy` (bundling and deploying) ship separately, with their own
versions. Every release on https://github.com/NDWX/Pug.Sqetch/releases carries self-contained
executables, so nothing but the dotnet tool needs .NET. Release tags are `sqetch-v<X.Y.Z>` and
`sqetch_deploy-v<X.Y.Z>` (an underscore), and pre-release tags are `sqetch-pre-v<X.Y.Z>` and
`sqetch_deploy-pre-v<X.Y.Z>`.

## First, look at what the repository already uses

Do not add a second way of installing. Look for:

| Found | Means | Run the tools as |
|---|---|---|
| `.config/mise/conf.d/sqetch*.toml`, or `sqetch` in `mise.toml` | mise | `sqetch …` with mise activated, otherwise `mise exec -- sqetch …` |
| `Pug.Sqetch.Cli` / `Pug.Sqetch.Deployment.Cli` in `dotnet-tools.json` | dotnet tool manifest | `dotnet sqetch …` after `dotnet tool restore` |
| `NDWX/Pug.Sqetch/actions/install-sqetch-deploy` in a workflow | the GitHub Action | `sqetch-deploy …` in later steps |
| `ghcr.io/ndwx/sqetch-deploy` or `ndwx/sqetch-deploy` | the container image | the image's entrypoint is `sqetch-deploy` |

If one is there, use it, at its pinned version. Install or upgrade only when asked.

## Choosing a method

| Situation | Method | Details |
|---|---|---|
| A repository, pinned for the team and its pipeline, no .NET needed | **mise**, from the release's own mise file | `references/mise.md` |
| A repository whose team already has the .NET 10 SDK | **dotnet tool manifest** | `references/dotnet-tool.md` |
| GitHub Actions (`sqetch-deploy`) | **`install-sqetch-deploy` action** | `references/github-action.md` |
| GitLab CI, other CI, Kubernetes jobs (`sqetch-deploy`) | **container image** | `references/container.md` |
| One machine, outside any repository | archive, MSI, deb or rpm | `references/downloads.md` |

Usually a developer needs both tools, and a deployment pipeline only `sqetch-deploy`. The
container image and the GitHub Action exist only for `sqetch-deploy`.

## After installing

```sh
sqetch --version
sqetch-deploy --version
```

Then use the `sqetch` skill's version rules: keep `sqetch-deploy` at least as new as `sqetch`.
