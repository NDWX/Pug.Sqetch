# Installing as a dotnet tool

Needs the .NET 10 SDK. The packages on nuget.org are `Pug.Sqetch.Cli` (`sqetch`) and
`Pug.Sqetch.Deployment.Cli` (`sqetch-deploy`). They are framework-dependent, unlike the release
archives.

## Per repository (preferred)

A tool manifest pins the versions for everyone working in the repository, and its pipeline:

```sh
dotnet new tool-manifest                        # creates dotnet-tools.json; commit it
dotnet tool install Pug.Sqetch.Cli              # recorded in the manifest
dotnet tool install Pug.Sqetch.Deployment.Cli
```

After cloning, and as a pipeline step:

```sh
dotnet tool restore
dotnet sqetch --help
dotnet sqetch-deploy deploy bundle.zip --driver sqlite --sqlite-file app.db
```

- The manifest applies to its folder and everything below it.
- The tools are not on `PATH`: every command starts with `dotnet`.
- To move to a newer version, when asked: `dotnet tool update Pug.Sqetch.Cli`, then commit the
  changed manifest.

## Globally

On `PATH` as `sqetch` and `sqetch-deploy`, for the current user:

```sh
dotnet tool install -g Pug.Sqetch.Cli
dotnet tool install -g Pug.Sqetch.Deployment.Cli
```

## Pre-releases

`dotnet tool install` skips them. Add `--prerelease` for the newest, or pin one with
`--version 0.1.0-pre.202610172239`.
