using Npgsql;
using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.DatabaseDrivers.PostgreSql;
using Testcontainers.PostgreSql;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// One PostgreSQL container for a test class, and a fresh database inside it per test — a schema
/// would do for the journal table, but a database is what a maintainer deploys to, and the whole
/// point of these tests is that the deployment meets a real server rather than a convincing one.
///
/// Starting the container is deliberately allowed to throw: the skip decision is made at discovery
/// by <see cref="Docker"/>, so a failure here means Docker answered the probe and then would not
/// serve, which is a result worth seeing rather than hiding.
/// </summary>
public sealed class PostgresDatabase : IAsyncLifetime
{
	private readonly PostgreSqlContainer _container = new PostgreSqlBuilder( "postgres:17-alpine" ).Build();

	private bool _started;

	public async Task InitializeAsync()
	{
		if( !Docker.Available )
			return;

		await _container.StartAsync();

		_started = true;
	}

	public async Task DisposeAsync()
	{
		if( _started )
			await _container.DisposeAsync();
	}

	/// <summary>A driver onto a database of this test's own, created on the spot.</summary>
	public IDatabaseDriver Driver( out string connectionString, TimeSpan? stepScriptTimeout = null )
	{
		connectionString = CreateDatabase();

		return Driver( connectionString, stepScriptTimeout );
	}

	/// <summary>
	/// A fresh database in the container, returned as a connection string. Separate from
	/// <see cref="Driver(string, TimeSpan?)"/> because a deployment releases its driver when it is
	/// done, and a scenario that deploys more than once needs the next driver on the same database.
	/// </summary>
	public string CreateDatabase()
	{
		string name = $"d{Guid.NewGuid():N}";

		using( NpgsqlConnection admin = new ( _container.GetConnectionString() ) )
		{
			admin.Open();

			using NpgsqlCommand create = admin.CreateCommand();

			create.CommandText = $"create database \"{name}\"";

			create.ExecuteNonQuery();
		}

		return new NpgsqlConnectionStringBuilder( _container.GetConnectionString() ) { Database = name }
			.ConnectionString;
	}

	public static IDatabaseDriver Driver( string connectionString, TimeSpan? stepScriptTimeout = null )
		=> new PostgreSqlDatabaseDriverFactory().Create(
			new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["connection-string"] = connectionString },
			stepScriptTimeout ?? TimeSpan.FromSeconds( 30 ) );

	/// <summary>Reads committed state back through a connection of its own.</summary>
	public static List<string> Query( string connectionString, string sql )
	{
		using NpgsqlConnection connection = new ( connectionString );

		connection.Open();

		using NpgsqlCommand command = connection.CreateCommand();

		command.CommandText = sql;

		using NpgsqlDataReader reader = command.ExecuteReader();

		List<string> rows = [];

		while( reader.Read() )
			rows.Add(
				string.Join(
					"|",
					Enumerable.Range( 0, reader.FieldCount )
								.Select( index => reader.IsDBNull( index ) ? "<null>" : reader.GetValue( index ).ToString() ) ) );

		return rows;
	}
}
