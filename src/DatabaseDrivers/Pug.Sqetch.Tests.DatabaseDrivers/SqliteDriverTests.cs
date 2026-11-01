using System.Data;
using Microsoft.Data.Sqlite;
using Pug.Sqetch.Deployment;
using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.DatabaseDrivers.Sqlite;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// The driver against a real database, which is the only way these assertions mean anything: a fake
/// cannot tell us whether SQLite rolls DDL back, whether the provider matches a prefixless parameter
/// name, or whether an abandoned transaction leaves its work behind.
/// </summary>
public class SqliteDriverTests : IDisposable
{
	private readonly TempDatabase _database = new ();

	public void Dispose() => _database.Dispose();

	[Fact]
	public void ACommittedTransactionsWorkSurvivesIncludingItsDdl()
	{
		using IDatabaseDriver driver = _database.Driver();

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table thing ( id integer primary key )" );
		transaction.ExecuteStepScript( "insert into thing ( id ) values ( 1 )" );
		transaction.Commit();

		Assert.Equal( ["1"], _database.Query( "select id from thing" ) );
	}

	/// <summary>
	/// SQLite runs DDL inside a transaction like any other statement, so a rolled-back deployment
	/// leaves no half-created schema. Engines whose DDL commits itself are the reason the engine's
	/// rollback is compensating rather than transactional; this one does not need that.
	/// </summary>
	[Fact]
	public void ARolledBackTransactionLeavesNoTableBehind()
	{
		using IDatabaseDriver driver = _database.Driver();

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table thing ( id integer primary key )" );
		transaction.Rollback();

		Assert.Empty( _database.Query( "select name from sqlite_master where name = 'thing'" ) );
	}

	/// <summary>
	/// Disposing the driver must not commit for the caller: a deployment that threw between begin and
	/// commit is supposed to lose the work the open transaction held.
	/// </summary>
	[Fact]
	public void DisposingTheDriverRollsBackAnOpenTransaction()
	{
		using( IDatabaseDriver driver = _database.Driver() )
		{
			IDatabaseTransaction transaction = driver.BeginTransaction();

			transaction.ExecuteStepScript( "create table thing ( id integer primary key )" );
		}

		Assert.Empty( _database.Query( "select name from sqlite_master where name = 'thing'" ) );
	}

	[Fact]
	public void ASecondTransactionOnTheSameDriverIsRefusedWhileTheFirstIsOpen()
	{
		using IDatabaseDriver driver = _database.Driver();

		driver.BeginTransaction();

		InvalidOperationException error = Assert.Throws<InvalidOperationException>( () => driver.BeginTransaction() );

		Assert.Contains( "already open", error.Message );
	}

	[Fact]
	public void TheConnectionIsReusedSoOneTransactionFollowsAnother()
	{
		using IDatabaseDriver driver = _database.Driver();

		IDatabaseTransaction first = driver.BeginTransaction();

		first.ExecuteStepScript( "create table thing ( id integer primary key )" );
		first.Commit();

		IDatabaseTransaction second = driver.BeginTransaction();

		second.ExecuteStepScript( "insert into thing ( id ) values ( 2 )" );
		second.Commit();

		Assert.Equal( ["2"], _database.Query( "select id from thing" ) );
	}

	/// <summary>
	/// A step script is a whole 'deploy.sql', so it holds as many statements as its author wrote and
	/// runs as one command.
	/// </summary>
	[Fact]
	public void AStepScriptMayHoldSeveralStatements()
	{
		using IDatabaseDriver driver = _database.Driver();

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript(
			"""
			create table thing ( id integer primary key, name text );
			insert into thing ( id, name ) values ( 1, 'a' );
			insert into thing ( id, name ) values ( 2, 'b' );
			""" );

		transaction.Commit();

		Assert.Equal( ["1|a", "2|b"], _database.Query( "select id, name from thing order by id" ) );
	}

	// ------------------------------------------------------------------ journaling parameters

	/// <summary>
	/// Sqetch supplies bare names and leaves the prefix to the driver. This pins what SQLite accepts,
	/// which is why the driver binds a name as it stands: all three placeholder forms resolve a
	/// prefixless <see cref="SqliteParameter"/>.
	/// </summary>
	[Theory]
	[InlineData( "@release" )]
	[InlineData( ":release" )]
	[InlineData( "$release" )]
	public void APrefixlessParameterNameBindsToEveryPlaceholderFormSqliteAccepts( string placeholder )
	{
		using IDatabaseDriver driver = _database.Driver();

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table journal ( release text )" );
		transaction.ExecuteJournalingStatement(
			$"insert into journal ( release ) values ( {placeholder} )", [new JournalingParameter( "release", "r1" )] );

		transaction.Commit();

		Assert.Equal( ["r1"], _database.Query( "select release from journal" ) );
	}

	/// <summary>
	/// Every parameter a slot carries is supplied whether the maintainer's statement mentions it or
	/// not, so a provider that refused the unmentioned ones would need the driver to scan the SQL
	/// first. This pins that SQLite does not, which is why no driver scans.
	/// </summary>
	[Fact]
	public void AParameterTheStatementNeverMentionsIsIgnoredRatherThanRefused()
	{
		using IDatabaseDriver driver = _database.Driver();

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table journal ( release text )" );
		transaction.ExecuteJournalingStatement(
			"insert into journal ( release ) values ( @release )",
			[
				new JournalingParameter( "release", "r1" ),
				new JournalingParameter( "description", "never mentioned" ),
				new JournalingParameter( "utcTimestamp", DateTime.UtcNow )
			] );

		transaction.Commit();

		Assert.Equal( ["r1"], _database.Query( "select release from journal" ) );
	}

	/// <summary>
	/// The engine binds 'utcTimestamp' as a <see cref="DateTime"/> rather than as text precisely so a
	/// driver can hand it to the provider as one; here that means it comes back as a date, not as
	/// whatever string a format would have produced.
	/// </summary>
	[Fact]
	public void AUtcTimestampIsStoredAsADateAndReadsBackAsTheSameInstant()
	{
		DateTime instant = new ( 2026, 10, 1, 12, 34, 56, DateTimeKind.Utc );

		using IDatabaseDriver driver = _database.Driver();

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table journal ( at text )" );
		transaction.ExecuteJournalingStatement(
			"insert into journal ( at ) values ( @utcTimestamp )",
			[new JournalingParameter( "utcTimestamp", instant )] );

		transaction.Commit();

		using SqliteConnection connection = new ( $"Data Source={_database.File}" );

		connection.Open();

		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = "select at from journal";

		Assert.Equal( instant, Assert.IsType<DateTime>( command.ExecuteScalar() is string text ? DateTime.Parse( text ) : null ) );
	}

	[Fact]
	public void AJournalingQueryReturnsAReaderTheCallerDisposes()
	{
		using IDatabaseDriver driver = _database.Driver();

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript(
			"""
			create table journal ( release text, completed integer );
			insert into journal ( release, completed ) values ( 'r1', 1 );
			""" );

		using( IDataReader reader = transaction.ExecuteJournalingQuery(
					"select release, completed from journal where release = @release",
					[new JournalingParameter( "release", "r1" )] ) )
		{
			Assert.True( reader.Read() );
			Assert.Equal( "r1", reader.GetValue( 0 ) );
			Assert.Equal( 1L, reader.GetValue( 1 ) );
			Assert.False( reader.Read() );
		}

		transaction.Commit();
	}

	/// <summary>
	/// A reader the caller forgot would hold the statement open; SQLite then refuses to commit, so
	/// the transaction closes what it handed out as it ends.
	/// </summary>
	[Fact]
	public void ATransactionClosesAReaderTheCallerLeftOpen()
	{
		using IDatabaseDriver driver = _database.Driver();

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table journal ( release text )" );
		transaction.ExecuteJournalingStatement(
			"insert into journal ( release ) values ( @release )", [new JournalingParameter( "release", "r1" )] );

		IDataReader leaked = transaction.ExecuteJournalingQuery( "select release from journal", [] );

		Assert.True( leaked.Read() );

		transaction.Commit();

		Assert.True( leaked.IsClosed );
		Assert.Equal( ["r1"], _database.Query( "select release from journal" ) );
	}

	// ------------------------------------------------------------------ the factory

	[Fact]
	public void TheDriverIsNamedSqliteAndTakesEitherAFileOrAConnectionString()
	{
		SqliteDatabaseDriverFactory factory = new ();

		Assert.Equal( "sqlite", factory.Name );

		DatabaseDriverParametersDefinition definition = factory.GetParametersDefinition();

		Assert.Equal( ["file", "connection-string"], definition.Parameters.Select( parameter => parameter.Name ) );
		Assert.Equal( [["file"], ["connection-string"]], definition.RequiredParametersOptions );
	}

	[Fact]
	public void AFileThatDoesNotExistYetIsCreated()
	{
		Assert.False( System.IO.File.Exists( _database.File ) );

		using IDatabaseDriver driver = _database.Driver();

		driver.BeginTransaction().Commit();

		Assert.True( System.IO.File.Exists( _database.File ) );
	}

	/// <summary>
	/// The escape hatch for everything 'file' does not cover — including refusing to create a
	/// database that is not there, which is how a mistyped path is caught. A driver is configured
	/// without connecting, so the refusal arrives on first use as a connection failure, with the
	/// provider's own error kept underneath it for whoever needs the detail.
	/// </summary>
	[Fact]
	public void AConnectionStringInReadWriteModeRefusesAMissingFile()
	{
		using IDatabaseDriver driver = new SqliteDatabaseDriverFactory().Create(
			new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
			{
				["connection-string"] = $"Data Source={_database.File};Mode=ReadWrite"
			},
			TimeSpan.FromSeconds( 30 ) );

		DatabaseConnectionException error =
			Assert.Throws<DatabaseConnectionException>( () => driver.BeginTransaction() );

		Assert.IsType<SqliteException>( error.InnerException );
		Assert.Contains( "could not connect to the database", error.Message );
	}

	[Fact]
	public void AFileAndAConnectionStringTogetherAreRefused()
	{
		ArgumentException error = Assert.Throws<ArgumentException>(
			() => new SqliteDatabaseDriverFactory().Create(
				new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
				{
					["file"] = _database.File, ["connection-string"] = $"Data Source={_database.File}"
				},
				TimeSpan.FromSeconds( 30 ) ) );

		Assert.Contains( "not both", error.Message );
	}

	[Fact]
	public void AConnectionStringWithNoDataSourceIsRefused()
	{
		ArgumentException error = Assert.Throws<ArgumentException>(
			() => new SqliteDatabaseDriverFactory().Create(
				new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["connection-string"] = "Cache=Shared" },
				TimeSpan.FromSeconds( 30 ) ) );

		Assert.Contains( "no data source", error.Message );
	}

	[Fact]
	public void TheRegistryExtensionRegistersTheDriverUnderItsName()
	{
		DatabaseDriverRegistry registry = new ();

		registry.RegisterSqliteDriver();

		Assert.Equal( ["sqlite"], registry.Names );
		Assert.IsType<SqliteDatabaseDriverFactory>( registry.Create( "sqlite" ) );
	}
}
