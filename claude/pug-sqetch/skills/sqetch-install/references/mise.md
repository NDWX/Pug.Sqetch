# Installing with mise

[mise](https://mise.jdx.dev) installs both tools from the release archives and pins their versions
in the repository, so everyone working on it, and its pipeline, runs the same ones. Two ways: the
mise file each release carries (simplest), or a configuration written by hand.

## From the release's mise file

Every release carries `<name>-<v>.mise.toml`, naming each platform's archive by its download URL
and SHA-256. Save it in `.config/mise/conf.d/`, commit it, and install. For `sqetch-deploy` 0.1.0:

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

For `sqetch`, the tag is `sqetch-v<v>` and the file `sqetch-<v>.mise.toml`; save it as
`.config/mise/conf.d/sqetch.toml`.

- One file covers Linux, macOS and Windows on x64 and arm64.
- mise merges `conf.d` files with an existing `mise.toml`; neither needs editing.
- To change version, replace the file with another release's and run `mise install`.
- A pre-release's file works the same. Its URL keeps the tag's `-pre-v<X.Y.Z>`, and its file name the
  full version, such as `sqetch-deploy-0.1.0-pre.202610172239.mise.toml`.
- mise refuses a download whose SHA-256 differs from the file's, and makes no GitHub API calls, so
  no token is needed.

To find the latest version, look at https://github.com/NDWX/Pug.Sqetch/releases or ask the user;
do not guess.

## By hand

To keep everything in one `mise.toml`, add this at the repository root (or merge it in):

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

Leave out a tool the repository doesn't need, then `mise install`.

- Keep the per-platform table. A single templated `asset_pattern` breaks `mise lock`, and a looser
  pattern also matches the installers a release carries.
- The versions of the two tools need not match.
- For a pre-release, pin its `X.Y.Z` and change the prefix to `sqetch-pre-v` or
  `sqetch_deploy-pre-v`.
- `mise lock` records every platform's checksum in `mise.lock`; commit it.
- This way reads releases through the GitHub API, which limits anonymous requests: set
  `GITHUB_TOKEN` in pipelines.

## Running the tools

With mise activated in the shell, `sqetch` and `sqetch-deploy` are on `PATH` inside the repository
only. Without activation, as in most scripts and pipelines: `mise exec -- sqetch-deploy deploy …`.
