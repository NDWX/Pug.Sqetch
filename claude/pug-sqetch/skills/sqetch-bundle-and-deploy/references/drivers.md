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

## Both

- A server that cannot be reached is exit code `33`. The driver connects only when the deployment
  begins, so wrong switches that still form a valid configuration surface then, not earlier.
- Both engines run DDL inside transactions, so a failing release at the default commit level leaves
  nothing behind.
