# Sqetch

A database schema upgrader for DevOps pipelines, made of two command-line tools:

- **`sqetch`** authors a project: plans of schema change, each made of steps with deploy, verify and
  rollback scripts, grouped into releases.
- **`sqetch-deploy`** bundles a project for a pipeline and deploys bundles to a database (SQLite and
  PostgreSQL), journaling what it deployed through SQL the project defines.

The two tools are versioned and released separately.

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
| .NET 10 | nuget.org package `Pug.Sqetch.Cli` | See [As a dotnet tool](#as-a-dotnet-tool) |

`sqetch-deploy` ships the same way, with `sqetch-deploy` in place of `sqetch`. Its dotnet tool
package is `Pug.Sqetch.Deployment.Cli`. Check downloads against the release's `SHA256SUMS`. Pre-release
files carry versions like `0.1.0-pre.202610172239`. Winget, Chocolatey and Homebrew packages are not
published yet.

The archives hold a self-contained single executable. Keep `sqetch-deploy`'s folder together: the
native SQLite library sits beside the executable.

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
