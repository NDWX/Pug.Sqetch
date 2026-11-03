using MySqlConnector;

using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.DatabaseDrivers.Ado;

namespace Pug.Sqetch.DatabaseDrivers.MySql;

/// <summary>
/// Deploys to MySQL and MariaDB through MySqlConnector.
///
/// **Both commit DDL implicitly**: a CREATE, ALTER or DROP ends the open transaction, committing
/// everything before it — the journal rows included — and the statements after it run in a new one.
/// A release deployed at release commit level is therefore not all-or-nothing here, and a failing
/// step can leave the schema changes before it in place. The engine's compensating rollback
/// (<c>deploy --rollback</c>) is what undoes them; it is itself not atomic either, so a failing
/// rollback script can leave compensation half done, which the transactional engines cannot.
///
/// Connection strings the driver builds allow user variables, so a step script may use
/// <c>@variable</c> without MySqlConnector taking it for a missing parameter. Journaled instants are
/// written as given — UTC — so the journal column is <c>datetime(6)</c>; a <c>timestamp</c> column
/// would convert them through the session's time zone.
/// </summary>
public sealed class MySqlDatabaseDriverFactory : IDatabaseDriverFactory, IJournalingTemplateProvider
{
	public const string DriverName = "mysql";

	public string Name => DriverName;

	/// <summary>The rollback-aware template under 'Journaling/', one file per slot.</summary>
	public IReadOnlyDictionary<string, string> GetJournalingTemplate()
		=> EmbeddedJournalingTemplate.Load( typeof(MySqlDatabaseDriverFactory).Assembly );

	public DatabaseDriverParametersDefinition GetParametersDefinition()
		=> new(
			[
				new DatabaseDriverParameterDefinition( "host", "Server host name or address" ),
				new DatabaseDriverParameterDefinition( "port", "Server port; defaults to 3306" ),
				new DatabaseDriverParameterDefinition( "database", "Database (schema) name" ),
				new DatabaseDriverParameterDefinition( "username", "User to connect as" ),
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
					ServerConnectionParameters.ConnectionStringName,
					"Full MySqlConnector connection string, for anything the parameters above do not cover"
				)
			],
			[ [ "host", "database" ], [ ServerConnectionParameters.ConnectionStringName ] ]
		);

	public IDatabaseDriver Create( IDictionary<string, string> parameters, TimeSpan stepScriptTimeout )
	{
		string connectionString = ConnectionString( parameters );

		// MySqlConnector matches a prefixless parameter against '@name' and '?name' and ignores one the
		// statement never mentions, so the plain ADO.NET binding serves it as it stands
		return new AdoDatabaseDriver( () => new MySqlConnection( connectionString ), stepScriptTimeout );
	}

	private static string ConnectionString( IDictionary<string, string> parameters )
	{
		ServerConnectionParameters given = new( parameters );

		if( given.ConnectionString( "host", "port", "database", "username", "password", "password-env" ) is { } supplied )
			return new MySqlConnectionStringBuilder( supplied ).ConnectionString;

		MySqlConnectionStringBuilder builder = new()
		{
			Server = given.Required( "host" ),
			Database = given.Required( "database" ),
			AllowUserVariables = true
		};

		if( given.Port() is { } port )
			builder.Port = (uint)port;

		if( given.Value( "username" ) is { } username )
			builder.UserID = username;

		if( given.Password() is { } password )
			builder.Password = password;

		return builder.ConnectionString;
	}
}
