using System.Data;

using Microsoft.Data.SqlClient;

using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.DatabaseDrivers.Ado;

namespace Pug.Sqetch.DatabaseDrivers.SqlServer;

/// <summary>
/// Deploys to SQL Server (and Azure SQL) through Microsoft.Data.SqlClient. SQL Server runs DDL inside a
/// transaction, so a release deployed at release commit level is all-or-nothing here.
///
/// Step scripts are split into batches at 'GO' lines (<see cref="SqlServerBatches"/>), which then run
/// in order in the step's transaction. Journaled timestamps are bound as <c>datetime2</c>: SqlClient
/// maps a <see cref="DateTime"/> to <c>datetime</c> by default, which rounds to a third of a
/// millisecond, so the instant the host supplied would not be the one stored.
///
/// SqlClient encrypts connections by default, so a server with a self-signed certificate — a
/// development container, typically — needs 'trust-server-certificate'.
///
/// On Windows, SqlClient's networking is normally a native library (Microsoft.Data.SqlClient.SNI.dll)
/// under proprietary licence terms that Sqetch's GPL/AGPL distribution cannot meet, so the build
/// leaves it out (Directory.Build.targets) and this driver switches SqlClient to its managed
/// networking — the implementation Linux and macOS always use. The one documented difference is
/// that Windows Authentication works only on domain-joined machines.
/// </summary>
public sealed class SqlServerDatabaseDriverFactory : IDatabaseDriverFactory, IJournalingTemplateProvider
{
	public const string DriverName = "sqlserver";

	public const string ManagedNetworkingSwitch = "Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows";

	// before SqlClient is first touched: it reads its switches once and caches them
	static SqlServerDatabaseDriverFactory() => AppContext.SetSwitch( ManagedNetworkingSwitch, true );

	private static readonly AdoDriverOptions Options = new()
	{
		SplitStepScript = SqlServerBatches.Split,
		ConfigureParameter = ( bound, parameter ) => {
			if( parameter.Value is DateTime )
				bound.DbType = DbType.DateTime2;
		}
	};

	public string Name => DriverName;

	/// <summary>The rollback-aware template under 'Journaling/', one file per slot.</summary>
	public IReadOnlyDictionary<string, string> GetJournalingTemplate()
		=> EmbeddedJournalingTemplate.Load( typeof(SqlServerDatabaseDriverFactory).Assembly );

	public DatabaseDriverParametersDefinition GetParametersDefinition()
		=> new(
			[
				new DatabaseDriverParameterDefinition( "host", "Server host name or address" ),
				new DatabaseDriverParameterDefinition( "port", "Server port; defaults to 1433" ),
				new DatabaseDriverParameterDefinition( "database", "Database name" ),
				new DatabaseDriverParameterDefinition(
					"username",
					"SQL Server login; when omitted, integrated (Windows or Kerberos) authentication is used"
				),
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
					"trust-server-certificate",
					"'true' to accept a server certificate that does not validate, such as a development " +
					"server's self-signed one; connections are encrypted either way"
				),
				new DatabaseDriverParameterDefinition(
					ServerConnectionParameters.ConnectionStringName,
					"Full Microsoft.Data.SqlClient connection string, for anything the parameters above do " +
					"not cover, such as Microsoft Entra authentication"
				)
			],
			[ [ "host", "database" ], [ ServerConnectionParameters.ConnectionStringName ] ]
		);

	public IDatabaseDriver Create( IDictionary<string, string> parameters, TimeSpan stepScriptTimeout )
	{
		string connectionString = ConnectionString( parameters );

		return new AdoDatabaseDriver( () => new SqlConnection( connectionString ), stepScriptTimeout, Options );
	}

	private static string ConnectionString( IDictionary<string, string> parameters )
	{
		ServerConnectionParameters given = new( parameters );

		if( given.ConnectionString(
				"host", "port", "database", "username", "password", "password-env", "trust-server-certificate" ) is
			{ } supplied )
			return new SqlConnectionStringBuilder( supplied ).ConnectionString;

		string host = given.Required( "host" );

		SqlConnectionStringBuilder builder = new()
		{
			DataSource = given.Port() is { } port ? $"{host},{port}" : host,
			InitialCatalog = given.Required( "database" )
		};

		if( given.Value( "username" ) is { } username )
		{
			builder.UserID = username;
			builder.Password = given.Password() ?? "";
		}
		else
		{
			if( given.Password() is not null )
				throw new ArgumentException( "'password' and 'password-env' need 'username'." );

			builder.IntegratedSecurity = true;
		}

		if( given.Value( "trust-server-certificate" ) is { } trust )
			builder.TrustServerCertificate = bool.TryParse( trust, out bool parsed ) ?
				parsed :
				throw new ArgumentException( $"'trust-server-certificate' must be 'true' or 'false', not '{trust}'." );

		return builder.ConnectionString;
	}
}
