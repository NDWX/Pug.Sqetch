using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.DatabaseDrivers.Sqlite;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// A real SQLite database in a temp file, deleted with the test. A file rather than ':memory:',
/// whose contents live only as long as the connection that opened it — the driver keeps one
/// connection, so an in-memory database would pass tests that a reopened one would fail.
/// </summary>
public sealed class TempDatabase : IDisposable
{
	private readonly string _directory =
		Path.Combine( Path.GetTempPath(), $"sqetch-sqlite-{Guid.NewGuid():N}" );

	public TempDatabase() => Directory.CreateDirectory( _directory );

	public string File => Path.Combine( _directory, "deploy.db" );

	public IDatabaseDriver Driver( TimeSpan? stepScriptTimeout = null )
		=> new SqliteDatabaseDriverFactory().Create(
			new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["file"] = File },
			stepScriptTimeout ?? TimeSpan.FromSeconds( 30 ) );

	/// <summary>Reads the database back through a connection of its own, so only committed state is seen.</summary>
	public List<string> Query( string sql )
	{
		using Microsoft.Data.Sqlite.SqliteConnection connection = new ( $"Data Source={File}" );

		connection.Open();

		using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();

		command.CommandText = sql;

		using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();

		List<string> rows = [];

		while( reader.Read() )
			rows.Add(
				string.Join(
					"|",
					Enumerable.Range( 0, reader.FieldCount )
								.Select( index => reader.IsDBNull( index ) ? "<null>" : reader.GetValue( index ).ToString() ) ) );

		return rows;
	}

	public void Dispose()
	{
		// the pool holds the file open until it is cleared, and Windows would refuse the delete
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

		try
		{
			Directory.Delete( _directory, recursive: true );
		}
		catch( IOException )
		{
		}
	}
}
