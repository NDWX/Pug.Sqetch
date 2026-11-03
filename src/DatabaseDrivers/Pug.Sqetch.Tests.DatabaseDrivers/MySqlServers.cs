using DotNet.Testcontainers.Containers;

using MySqlConnector;

using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.DatabaseDrivers.MySql;

using Testcontainers.MariaDb;
using Testcontainers.MySql;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// One MySQL-family server in a container for a test class, and a fresh database inside it per test,
/// as <see cref="PostgresDatabase"/> does for PostgreSQL. The 'mysql' driver serves MySQL and MariaDB
/// alike, so every test runs against both (<see cref="MySqlServer"/>, <see cref="MariaDbServer"/>).
/// Connected as root, which creating databases needs.
/// </summary>
public abstract class MySqlFamilyServer : IAsyncLifetime
{
	protected const string RootPassword = "sqetch-test";

	private bool _started;

	protected abstract IDatabaseContainer Container { get; }

	protected abstract ushort ContainerPort { get; }

	public async Task InitializeAsync()
	{
		if( !Docker.Available )
			return;

		await ( (IContainer)Container ).StartAsync();

		_started = true;
	}

	public async Task DisposeAsync()
	{
		if( _started )
			await ( (IContainer)Container ).DisposeAsync();
	}

	/// <summary>A driver onto a database of this test's own, created on the spot.</summary>
	public IDatabaseDriver Driver( out string connectionString, TimeSpan? stepScriptTimeout = null )
	{
		connectionString = CreateDatabase( out _ );

		return Driver( connectionString, stepScriptTimeout );
	}

	/// <summary>A fresh database in the container, returned as a connection string.</summary>
	public string CreateDatabase( out string name )
	{
		name = $"d{Guid.NewGuid():N}";

		using( MySqlConnection admin = new( Container.GetConnectionString() ) )
		{
			admin.Open();

			using MySqlCommand create = admin.CreateCommand();

			create.CommandText = $"create database `{name}`";

			create.ExecuteNonQuery();
		}

		return new MySqlConnectionStringBuilder( Container.GetConnectionString() ) { Database = name }.ConnectionString;
	}

	/// <summary>
	/// A driver built from the individual parameters rather than a connection string — the form that
	/// gets the driver's own connection settings, such as allowing user variables.
	/// </summary>
	public IDatabaseDriver DriverFromParameters( string database )
		=> new MySqlDatabaseDriverFactory().Create(
			new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
			{
				["host"] = ( (IContainer)Container ).Hostname,
				["port"] = ( (IContainer)Container ).GetMappedPublicPort( ContainerPort ).ToString(),
				["database"] = database,
				["username"] = "root",
				["password"] = RootPassword
			},
			TimeSpan.FromSeconds( 30 )
		);

	public static IDatabaseDriver Driver( string connectionString, TimeSpan? stepScriptTimeout = null )
		=> new MySqlDatabaseDriverFactory().Create(
			new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
				{ ["connection-string"] = connectionString },
			stepScriptTimeout ?? TimeSpan.FromSeconds( 30 )
		);

	/// <summary>Reads committed state back through a connection of its own.</summary>
	public static List<string> Query( string connectionString, string sql )
	{
		using MySqlConnection connection = new( connectionString );

		connection.Open();

		using MySqlCommand command = connection.CreateCommand();

		command.CommandText = sql;

		using MySqlDataReader reader = command.ExecuteReader();

		List<string> rows = [ ];

		while( reader.Read() )
			rows.Add(
				string.Join(
					"|",
					Enumerable.Range( 0, reader.FieldCount )
						.Select( index => reader.IsDBNull( index ) ? "<null>" : reader.GetValue( index ).ToString() )
				)
			);

		return rows;
	}
}

public sealed class MySqlServer : MySqlFamilyServer
{
	private readonly MySqlContainer _container = new MySqlBuilder( "mysql:8.4" )
		.WithUsername( "root" )
		.WithPassword( RootPassword )
		.Build();

	protected override IDatabaseContainer Container => _container;

	protected override ushort ContainerPort => MySqlBuilder.MySqlPort;
}

public sealed class MariaDbServer : MySqlFamilyServer
{
	private readonly MariaDbContainer _container = new MariaDbBuilder( "mariadb:11" )
		.WithUsername( "root" )
		.WithPassword( RootPassword )
		.Build();

	protected override IDatabaseContainer Container => _container;

	protected override ushort ContainerPort => MariaDbBuilder.MariaDbPort;
}
