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
public sealed class PostgreSqlDatabaseDriverFactory : IDatabaseDriverFactory, IJournalingTemplateProvider
{
	public const string DriverName = "postgres";

	public string Name => DriverName;

	/// <summary>The rollback-aware template under 'Journaling/', one file per slot.</summary>
	public IReadOnlyDictionary<string, string> GetJournalingTemplate()
		=> EmbeddedJournalingTemplate.Load( typeof(PostgreSqlDatabaseDriverFactory).Assembly );

	public DatabaseDriverParametersDefinition GetParametersDefinition()
		=> new(
			[
				new DatabaseDriverParameterDefinition( "host", "Server host name or address" ),
				new DatabaseDriverParameterDefinition( "port", "Server port; defaults to 5432" ),
				new DatabaseDriverParameterDefinition( "database", "Database name" ),
				new DatabaseDriverParameterDefinition( "username", "Role to connect as" ),
				new DatabaseDriverParameterDefinition(
					"password",
					"Password for 'username'; prefer 'password-env', since a command line is visible to " +
					"every process on the host"
				),
				new DatabaseDriverParameterDefinition(
					"password-env",
					"Name of an environment variable holding the password"
				),
				new DatabaseDriverParameterDefinition(
					"connection-string",
					"Full Npgsql connection string, for anything the parameters above do not cover"
				)
			],
			[ [ "host", "database" ], [ "connection-string" ] ]
		);

	public IDatabaseDriver Create( IDictionary<string, string> parameters, TimeSpan stepScriptTimeout )
	{
		string connectionString = ConnectionString( parameters );

		// Npgsql matches a prefixless parameter against the placeholder forms it accepts and ignores
		// one the statement never mentions — it rewrites named placeholders to positional ones, but
		// only for the names it found — so the plain ADO.NET binding serves it as it stands
		return new AdoDatabaseDriver( () => new NpgsqlConnection( connectionString ), stepScriptTimeout );
	}

	private static string ConnectionString( IDictionary<string, string> parameters )
	{
		ServerConnectionParameters given = new( parameters );

		if( given.ConnectionString( "host", "port", "database", "username", "password", "password-env" ) is { } supplied )
			return new NpgsqlConnectionStringBuilder( supplied ).ConnectionString;

		NpgsqlConnectionStringBuilder builder = new() { Host = given.Required( "host" ), Database = given.Required( "database" ) };

		if( given.Port() is { } port )
			builder.Port = port;

		if( given.Value( "username" ) is { } username )
			builder.Username = username;

		builder.Password = given.Password();

		return builder.ConnectionString;
	}
}