# Licensing

Every project in this repository is licensed under the
[GNU Affero General Public License, version 3 or (at your option) any later
version](LICENSE) (`AGPL-3.0-or-later`), with one exception: the command-line
layer, the packaging sources and the GitHub Actions are licensed under the
[GNU General Public License, version 3 or (at your option) any later
version](Installers/LICENSE) (`GPL-3.0-or-later`).

Copyright holder: Pug.

Each GPL folder carries its own `LICENSE` file with the GPL text; the
repository-root `LICENSE` is the AGPL text and applies to everything else.

## Licence by project

| Folder | Licence |
|---|---|
| `Pug.Sqetch.Planning.Models` | AGPL-3.0-or-later |
| `Pug.Sqetch.Planning.Abstractions` | AGPL-3.0-or-later |
| `Pug.Sqetch.Planning` | AGPL-3.0-or-later |
| `Pug.Sqetch.Planning.ProjectInfoStores.FileSystem` | AGPL-3.0-or-later |
| `Pug.Sqetch.Deployment.Abstractions` | AGPL-3.0-or-later |
| `Pug.Sqetch.Deployment.Bundling.Abstractions` | AGPL-3.0-or-later |
| `Pug.Sqetch.Deployment.Bundling` | AGPL-3.0-or-later |
| `Pug.Sqetch.Deployment.Bundling.BundleTypes` | AGPL-3.0-or-later |
| `Pug.Sqetch.Deployment.Bundling.Layouts` | AGPL-3.0-or-later |
| `Pug.Sqetch.Deployment` | AGPL-3.0-or-later |
| `Pug.Sqetch.DatabaseDriver.Abstractions` | AGPL-3.0-or-later |
| `Pug.Sqetch.DatabaseDrivers.Ado` | AGPL-3.0-or-later |
| `Pug.Sqetch.DatabaseDrivers.Sqlite` | AGPL-3.0-or-later |
| `Pug.Sqetch.DatabaseDrivers.PostgreSql` | AGPL-3.0-or-later |
| `Pug.Sqetch.Cli.Common` | GPL-3.0-or-later |
| `Pug.Sqetch.Cli.Commands` | GPL-3.0-or-later |
| `Pug.Sqetch.Planning.Cli` | GPL-3.0-or-later |
| `Pug.Sqetch.Deployment.Cli.Commands` | GPL-3.0-or-later |
| `Pug.Sqetch.Deployment.Cli` | GPL-3.0-or-later |
| `Pug.Sqetch.Planning.Tests` | AGPL-3.0-or-later |
| `Pug.Sqetch.Tests.Planning.Stores.FileSystem` | AGPL-3.0-or-later |
| `Pug.Sqetch.Tests.Deployment.Bundling` | AGPL-3.0-or-later |
| `Pug.Sqetch.Tests.Deployment` | AGPL-3.0-or-later |
| `Pug.Sqetch.Tests.DatabaseDrivers` | AGPL-3.0-or-later |
| `Pug.Sqetch.Tests.Cli.Common` | GPL-3.0-or-later |
| `Pug.Sqetch.Tests.Planning.Cli` | GPL-3.0-or-later |
| `Pug.Sqetch.Tests.Deployment.Cli` | GPL-3.0-or-later |
| `Installers` (WiX, nfpm, Chocolatey, build scripts) | GPL-3.0-or-later |
| `actions` (the GitHub Actions for installing the tools) | GPL-3.0-or-later |

## Distributed artifacts

The distributed CLI artifacts (archives, MSI, deb/rpm packages, the dotnet
tool and the Chocolatey package) combine both sets of projects, so their
package licence expression is:

    GPL-3.0-or-later AND AGPL-3.0-or-later

Third-party packages that end up in the CLI hosts' output are listed, with
their licences, in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
Self-contained artifacts also ship the .NET runtime's own `LICENSE` and
`THIRD-PARTY-NOTICES`; these are copied in at packaging time from the runtime
pack and are not stored in this repository.
