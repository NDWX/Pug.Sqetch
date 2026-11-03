using Microsoft.Data.SqlClient;

using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.DatabaseDrivers.SqlServer;

using Testcontainers.MsSql;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// One SQL Server container for a test class, and a fresh database inside it per test, as
/// <see cref="PostgresDatabase"/> does for PostgreSQL. The image comes from Microsoft's registry and
/// runs on amd64 only, which <see cref="DockerAmd64FactAttribute"/> checks before any test asks for it.
/// </summary>
public sealed class SqlServerDatabase : IAsyncLifetime
{
	private readonly MsSqlContainer _container = new MsSqlBuilder( "mcr.microsoft.com/mssql/server:2022-latest" ).Build();

	private bool _started;

	public async Task InitializeAsync()
	{
		if( !Docker.Available || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture !=
			System.Runtime.InteropServices.Architecture.X64 )
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

	/// <summary>A fresh database in the container, returned as a connection string.</summary>
	public string CreateDatabase()
	{
		string name = $"d{Guid.NewGuid():N}";

		using( SqlConnection admin = new( _container.GetConnectionString() ) )
		{
			admin.Open();

			using SqlCommand create = admin.CreateCommand();

			create.CommandText = $"create database [{name}]";

			create.ExecuteNonQuery();
		}

		return new SqlConnectionStringBuilder( _container.GetConnectionString() ) { InitialCatalog = name }
			.ConnectionString;
	}

	public static IDatabaseDriver Driver( string connectionString, TimeSpan? stepScriptTimeout = null )
		=> new SqlServerDatabaseDriverFactory().Create(
			new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
				{ ["connection-string"] = connectionString },
			stepScriptTimeout ?? TimeSpan.FromSeconds( 30 )
		);

	/// <summary>Reads committed state back through a connection of its own.</summary>
	public static List<string> Query( string connectionString, string sql )
	{
		using SqlConnection connection = new( connectionString );

		connection.Open();

		using SqlCommand command = connection.CreateCommand();

		command.CommandText = sql;

		using SqlDataReader reader = command.ExecuteReader();

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
