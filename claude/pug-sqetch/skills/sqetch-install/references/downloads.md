# Release archives and packages

For one machine, outside any repository. `<v>` is the version, for example `0.1.0`. The same files
exist for `sqetch-deploy`, with `sqetch-deploy` in place of `sqetch`, under the tag
`sqetch_deploy-v<v>`.

| Platform | File | Install |
|---|---|---|
| Linux, macOS | `sqetch-<v>-{linux,osx}-{x64,arm64}.tar.gz` | Extract the folder, put it on `PATH` |
| Windows | `sqetch-<v>-win-{x64,arm64}.zip` | Extract the folder, put it on `PATH` |
| Windows | `sqetch-<v>-win-{x64,arm64}.msi` | Run it; it adds the tool to `PATH` (per user or per machine) |
| Debian, Ubuntu | `sqetch_<v>_{amd64,arm64}.deb` | `sudo apt install ./sqetch_<v>_amd64.deb` |
| Fedora, RHEL | `sqetch-<v>.{x86_64,aarch64}.rpm` | `sudo dnf install ./sqetch-<v>.x86_64.rpm` |

Download URL: `https://github.com/NDWX/Pug.Sqetch/releases/download/<tag>/<file>`, for example
`https://github.com/NDWX/Pug.Sqetch/releases/download/sqetch-v0.1.0/sqetch-0.1.0-linux-x64.tar.gz`.

- Each archive holds one folder, `<name>-<v>/`, with a self-contained single executable. Keep
  `sqetch-deploy`'s folder together: the native SQLite library sits beside the executable.
- Check downloads against the release's `SHA256SUMS`:
  `sha256sum --check --ignore-missing SHA256SUMS`.
- Pre-release files carry versions like `0.1.0-pre.202610172239`. Inside deb and rpm packages that
  is spelled `0.1.0~pre.…`, so the final `0.1.0` upgrades over it.
- Winget, Chocolatey and Homebrew packages are not published yet.
- Installing system packages needs the user's consent; ask before running `sudo`.
