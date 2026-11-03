# Database drivers

`sqetch-deploy drivers list` and `sqetch-deploy drivers parameters <driver>` print this from the
installed tool; prefer them to this copy. A driver's switches are `--<driver>-<parameter>`, and its
*required sets* are alternatives: supply every switch of one set.

## sqlite

| Switch | Meaning |
|---|---|
| `--sqlite-file <PATH>` | the database file; created if it does not exist |
| `--sqlite-connection-string <TEXT>` | a full Microsoft.Data.Sqlite connection string |

Required sets: `--sqlite-file`, or `--sqlite-connection-string`; not both.

- To refuse a missing file, which is how a mistyped path is caught, use a connection string with
  `Mode=ReadWrite`: `--sqlite-connection-string "Data Source=app.db;Mode=ReadWrite"`.
- Keep `sqetch-deploy`'s folder together when installed from an archive: the native SQLite library
  sits beside the executable.

## postgres

| Switch | Meaning |
|---|---|
| `--postgres-host <HOST>` | server host name or address |
| `--postgres-port <PORT>` | server port; default 5432 |
| `--postgres-database <NAME>` | database name |
| `--postgres-username <ROLE>` | role to connect as |
| `--postgres-password-env <VARIABLE>` | name of an environment variable holding the password |
| `--postgres-password <TEXT>` | the password itself; avoid, a command line is visible to every process |
| `--postgres-connection-string <TEXT>` | a full Npgsql connection string |

Required sets: `--postgres-host` with `--postgres-database`, or `--postgres-connection-string`.

- A connection string *replaces* the other switches; it does not extend them. Put everything in it
  (`Host=…;Port=…;Database=…;Username=…;Password=…;SSL Mode=Require`), and keep it in a secret.
- The role needs the rights the step scripts use, plus creating and writing the journal table on
  first deployment.
- Each deployment uses one connection, so session settings a script makes carry over to later
  scripts in the same run.

## sqlserver (since 0.2)

For SQL Server and Azure SQL.

| Switch | Meaning |
|---|---|
| `--sqlserver-host <HOST>` | server host name or address |
| `--sqlserver-port <PORT>` | server port; default 1433 |
| `--sqlserver-database <NAME>` | database name |
| `--sqlserver-username <LOGIN>` | SQL Server login; when omitted, Windows (integrated) authentication |
| `--sqlserver-password-env <VARIABLE>` | name of an environment variable holding the password |
| `--sqlserver-password <TEXT>` | the password itself; avoid, a command line is visible to every process |
| `--sqlserver-trust-server-certificate <true\|false>` | accept a certificate that does not validate, such as a development server's self-signed one |
| `--sqlserver-connection-string <TEXT>` | a full Microsoft.Data.SqlClient connection string |

Required sets: `--sqlserver-host` with `--sqlserver-database`, or `--sqlserver-connection-string`.

- Connections are always encrypted; against a container or other self-signed server, pass
  `--sqlserver-trust-server-certificate true`.
- A connection string replaces the other switches. Use it for Microsoft Entra authentication
  (`Authentication=Active Directory Default`, for example) or anything else the switches lack.
- **Step scripts may use `GO`**: a line holding only `GO` (any case) separates batches, as in
  sqlcmd and SSMS, and the batches run in order in the step's transaction. `GO <count>` is not
  supported. Journaling statements never split on `GO`; they use `;;`.
- `sqetch-deploy` uses SqlClient's managed networking on every platform. Windows Authentication
  therefore works only on domain-joined machines; use a login or Entra otherwise.

## mysql (since 0.2)

For MySQL and MariaDB.

| Switch | Meaning |
|---|---|
| `--mysql-host <HOST>` | server host name or address |
| `--mysql-port <PORT>` | server port; default 3306 |
| `--mysql-database <NAME>` | database (schema) name |
| `--mysql-username <USER>` | user to connect as |
| `--mysql-password-env <VARIABLE>` | name of an environment variable holding the password |
| `--mysql-password <TEXT>` | the password itself; avoid, a command line is visible to every process |
| `--mysql-connection-string <TEXT>` | a full MySqlConnector connection string |

Required sets: `--mysql-host` with `--mysql-database`, or `--mysql-connection-string`.

- **DDL commits implicitly.** A CREATE, ALTER or DROP ends the open transaction, journal rows
  included. A failing step can therefore leave earlier schema changes in place, a release is not
  all-or-nothing, and a failing rollback script can leave compensation half done. Keep each step to
  one schema change where possible, and use `--rollback on-error` in pipelines.
- Step scripts may use user variables (`set @n = 1;`) when the driver builds the connection; with
  `--mysql-connection-string`, add `AllowUserVariables=true` yourself.
- A connection string replaces the other switches.

## All drivers

- A server that cannot be reached is exit code `33`. The driver connects only when the deployment
  begins, so wrong switches that still form a valid configuration surface then, not earlier.
- SQLite, PostgreSQL and SQL Server run DDL inside transactions, so a failing release at the
  default commit level leaves nothing behind. MySQL and MariaDB do not; see `mysql` above.
