using Npgsql;
using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.DatabaseDrivers.Ado;

namespace Pug.Sqetch.DatabaseDrivers.PostgreSql;

/// <summary>
/// Deploys to PostgreSQL through Npgsql. PostgreSQL runs DDL inside a transaction, so a release
/// deployed at release commit level is genuinely all-or-nothing — which is not true of every engine
/// and is why the engine's own rollback compensates rather than relies on one.
///
/// Journaled timestamps are bound as UTC <see cref="DateTime"/>s, which Npgsql sends as
/// <c>timestamptz</c>. A journal column of type <c>timestamp</c> (without time zone) therefore
/// stores whatever the session's time zone makes of that instant; <c>timestamptz</c> is the type
/// that keeps it.
/// </summary>
public sealed class PostgreSqlDatabaseDriverFactory : IDatabaseDriverFactory
{
	public const string DriverName = "postgres";

	public string Name => DriverName;

	public DatabaseDriverParametersDefinition GetParametersDefinition()
		=> new (
			[
				new DatabaseDriverParameterDefinition( "host", "Server host name or address" ),
				new DatabaseDriverParameterDefinition( "port", "Server port; defaults to 5432" ),
				new DatabaseDriverParameterDefinition( "database", "Database name" ),
				new DatabaseDriverParameterDefinition( "username", "Role to connect as" ),
				new DatabaseDriverParameterDefinition(
					"password", "Password for 'username'; prefer 'password-env', since a command line is visible to "
								+ "every process on the host" ),
				new DatabaseDriverParameterDefinition(
					"password-env", "Name of an environment variable holding the password" ),
				new DatabaseDriverParameterDefinition(
					"connection-string", "Full Npgsql connection string, for anything the parameters above do not cover" )
			],
			[["host", "database"], ["connection-string"]] );

	public IDatabaseDriver Create( IDictionary<string, string> parameters, TimeSpan stepScriptTimeout )
	{
		string connectionString = ConnectionString( parameters );

		// Npgsql matches a prefixless parameter against the placeholder forms it accepts and ignores
		// one the statement never mentions — it rewrites named placeholders to positional ones, but
		// only for the names it found — so the plain ADO.NET binding serves it as it stands
		return new AdoDatabaseDriver( () => new NpgsqlConnection( connectionString ), stepScriptTimeout );
	}

	/// <summary>
	/// 'connection-string' replaces the individual parameters rather than extending them: merging the
	/// two would leave it unclear which host won, and the whole point of the escape hatch is that the
	/// maintainer's string is used as written.
	/// </summary>
	private static string ConnectionString( IDictionary<string, string> parameters )
	{
		string? Value( string name )
			=> parameters.TryGetValue( name, out string? value ) && !string.IsNullOrWhiteSpace( value ) ? value : null;

		string? supplied = Value( "connection-string" );

		if( supplied is not null )
		{
			string[] conflicting = ["host", "port", "database", "username", "password", "password-env"];

			if( conflicting.Any( name => Value( name ) is not null ) )
				throw new ArgumentException(
					"'connection-string' replaces the individual connection parameters; specify one or the other." );

			return new NpgsqlConnectionStringBuilder( supplied ).ConnectionString;
		}

		string host = Value( "host" )
					?? throw new ArgumentException( "'host' is required unless 'connection-string' is given." );

		string database = Value( "database" )
						?? throw new ArgumentException( "'database' is required unless 'connection-string' is given." );

		NpgsqlConnectionStringBuilder builder = new () { Host = host, Database = database };

		if( Value( "port" ) is { } port )
			builder.Port = int.TryParse( port, out int parsed )
							? parsed
							: throw new ArgumentException( $"'port' must be a number, not '{port}'." );

		if( Value( "username" ) is { } username )
			builder.Username = username;

		builder.Password = Password( Value( "password" ), Value( "password-env" ) );

		return builder.ConnectionString;
	}

	/// <summary>
	/// The password, from the switch or from the environment variable the switch names. A command
	/// line is readable by every process on the host and lands in shell history, so the environment
	/// variable is the one to reach for in a pipeline; naming the variable rather than reading a
	/// fixed one keeps it the maintainer's choice.
	/// </summary>
	private static string? Password( string? password, string? variable )
	{
		if( password is not null && variable is not null )
			throw new ArgumentException( "specify either 'password' or 'password-env', not both." );

		if( variable is null )
			return password;

		string? value = Environment.GetEnvironmentVariable( variable );

		if( string.IsNullOrEmpty( value ) )
			throw new ArgumentException( $"environment variable '{variable}' named by 'password-env' is not set." );

		return value;
	}
}
