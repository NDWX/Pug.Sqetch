using System.Data.Common;
using Microsoft.Data.Sqlite;
using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.DatabaseDrivers.Ado;

namespace Pug.Sqetch.DatabaseDrivers.Sqlite;

/// <summary>
/// Deploys to a SQLite database file. SQLite runs DDL inside a transaction like any other
/// statement, so a release deployed at release commit level is genuinely all-or-nothing here —
/// unlike the engines whose DDL commits itself.
/// </summary>
public sealed class SqliteDatabaseDriverFactory : IDatabaseDriverFactory
{
	public const string DriverName = "sqlite";

	public string Name => DriverName;

	public DatabaseDriverParametersDefinition GetParametersDefinition()
		=> new (
			[
				new DatabaseDriverParameterDefinition( "file", "Path to the database file; created if it does not exist" ),
				new DatabaseDriverParameterDefinition(
					"connection-string",
					"Full Microsoft.Data.Sqlite connection string, for anything 'file' does not cover "
					+ "(pass 'Mode=ReadWrite' to refuse to create a missing file)" )
			],
			[["file"], ["connection-string"]] );

	public IDatabaseDriver Create( IDictionary<string, string> parameters, TimeSpan stepScriptTimeout )
	{
		string connectionString = ConnectionString( parameters );

		// SQLite accepts '@name', ':name' and '$name' alike and matches a prefixless SqliteParameter
		// against all three, and it ignores a parameter the statement never mentions, so the plain
		// ADO.NET binding serves it as it stands
		return new AdoDatabaseDriver( () => new SqliteConnection( connectionString ), stepScriptTimeout );
	}

	/// <summary>
	/// 'file' is the whole connection string for the ordinary case; 'connection-string' replaces it
	/// rather than extending it, so the two are mutually exclusive — merging them would leave it
	/// unclear which data source won.
	/// </summary>
	private static string ConnectionString( IDictionary<string, string> parameters )
	{
		parameters.TryGetValue( "file", out string? file );
		parameters.TryGetValue( "connection-string", out string? supplied );

		if( !string.IsNullOrWhiteSpace( file ) && !string.IsNullOrWhiteSpace( supplied ) )
			throw new ArgumentException( "specify either 'file' or 'connection-string', not both." );

		if( !string.IsNullOrWhiteSpace( supplied ) )
		{
			DbConnectionStringBuilder builder = new SqliteConnectionStringBuilder( supplied );

			if( string.IsNullOrWhiteSpace( ( (SqliteConnectionStringBuilder)builder ).DataSource ) )
				throw new ArgumentException( "'connection-string' names no data source." );

			return builder.ConnectionString;
		}

		if( string.IsNullOrWhiteSpace( file ) )
			throw new ArgumentException( "'file' is required unless 'connection-string' is given." );

		return new SqliteConnectionStringBuilder { DataSource = file }.ConnectionString;
	}
}
