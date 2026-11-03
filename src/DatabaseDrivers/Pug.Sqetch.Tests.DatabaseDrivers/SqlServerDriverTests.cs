using System.Data;

using Pug.Sqetch.Deployment;
using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.DatabaseDrivers.Ado;
using Pug.Sqetch.DatabaseDrivers.SqlServer;
using Pug.Sqetch.Shared;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// The driver against a real SQL Server in a container. Besides what every driver is asked, these
/// pin the two things this one does differently: step scripts split at 'GO' lines, and journaled
/// instants bound as 'datetime2', since SqlClient's default 'datetime' would round them.
/// </summary>
public class SqlServerDriverTests : IClassFixture<SqlServerDatabase>
{
	private const string TableExists = "select name from sys.tables where name = 'thing'";

	private readonly SqlServerDatabase _server;

	public SqlServerDriverTests( SqlServerDatabase server ) => _server = server;

	[DockerAmd64Fact]
	public void ACommittedTransactionsWorkSurvivesIncludingItsDdl()
	{
		using IDatabaseDriver driver = _server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table thing ( id integer primary key )" );
		transaction.ExecuteStepScript( "insert into thing ( id ) values ( 1 )" );
		transaction.Commit();

		Assert.Equal( [ "1" ], SqlServerDatabase.Query( connection, "select id from thing" ) );
	}

	/// <summary>SQL Server rolls DDL back with everything else, so a release is all-or-nothing here.</summary>
	[DockerAmd64Fact]
	public void ARolledBackTransactionLeavesNoTableBehind()
	{
		using IDatabaseDriver driver = _server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table thing ( id integer primary key )" );
		transaction.Rollback();

		Assert.Empty( SqlServerDatabase.Query( connection, TableExists ) );
	}

	[DockerAmd64Fact]
	public void DisposingTheDriverRollsBackAnOpenTransaction()
	{
		string connection;

		using( IDatabaseDriver driver = _server.Driver( out connection ) )
		{
			driver.BeginTransaction().ExecuteStepScript( "create table thing ( id integer primary key )" );
		}

		Assert.Empty( SqlServerDatabase.Query( connection, TableExists ) );
	}

	[DockerAmd64Fact]
	public void TheConnectionIsReusedSoOneTransactionFollowsAnother()
	{
		using IDatabaseDriver driver = _server.Driver( out string connection );

		IDatabaseTransaction first = driver.BeginTransaction();

		first.ExecuteStepScript( "create table thing ( id integer primary key )" );
		first.Commit();

		IDatabaseTransaction second = driver.BeginTransaction();

		second.ExecuteStepScript( "insert into thing ( id ) values ( 2 )" );
		second.Commit();

		Assert.Equal( [ "2" ], SqlServerDatabase.Query( connection, "select id from thing" ) );
	}

	[DockerAmd64Fact]
	public void AStepScriptMayHoldSeveralStatements()
	{
		using IDatabaseDriver driver = _server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript(
			"""
			create table thing ( id integer primary key, name nvarchar(10) );
			insert into thing ( id, name ) values ( 1, 'a' );
			insert into thing ( id, name ) values ( 2, 'b' );
			"""
		);

		transaction.Commit();

		Assert.Equal(
			[ "1|a", "2|b" ],
			SqlServerDatabase.Query( connection, "select id, name from thing order by id" )
		);
	}

	/// <summary>
	/// CREATE PROCEDURE must begin a batch, so two of them in one step need 'GO' between them — which
	/// the server itself would reject, so it is the driver that splits on it.
	/// </summary>
	[DockerAmd64Fact]
	public void GoLinesSplitAStepScriptIntoBatchesInOneTransaction()
	{
		using IDatabaseDriver driver = _server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript(
			"""
			create table thing ( id integer primary key );
			GO
			create procedure add_thing @id integer as insert into thing ( id ) values ( @id );
			  go
			create procedure count_things as select count( * ) from thing;
			GO
			exec add_thing 7;
			"""
		);

		transaction.Rollback();

		Assert.Empty( SqlServerDatabase.Query( connection, TableExists ) );
		Assert.Empty( SqlServerDatabase.Query( connection, "select name from sys.procedures" ) );

		IDatabaseTransaction committed = driver.BeginTransaction();

		committed.ExecuteStepScript(
			"""
			create table thing ( id integer primary key );
			GO
			create procedure add_thing @id integer as insert into thing ( id ) values ( @id );
			GO
			exec add_thing 7;
			"""
		);

		committed.Commit();

		Assert.Equal( [ "7" ], SqlServerDatabase.Query( connection, "select id from thing" ) );
	}

	[DockerAmd64Fact]
	public void AFailingBatchIsNamedByItsPosition()
	{
		using IDatabaseDriver driver = _server.Driver( out string _ );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		StepScriptBatchFailedException error = Assert.Throws<StepScriptBatchFailedException>(
			() => transaction.ExecuteStepScript( "create table thing ( id integer )\nGO\ninsert into nowhere values ( 1 )" ) );

		Assert.Equal( 2, error.Batch );
		Assert.Equal( 2, error.Batches );
		Assert.StartsWith( "batch 2 of 2 failed:", error.Message );
	}

	// ------------------------------------------------------------------ journaling parameters

	/// <summary>SqlClient adds the '@' to a parameter given without one, so Sqetch's bare names bind.</summary>
	[DockerAmd64Fact]
	public void APrefixlessParameterNameBindsToAnAtPlaceholder()
	{
		using IDatabaseDriver driver = _server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table journal ( release nvarchar(128) )" );

		transaction.ExecuteJournalingStatement(
			"insert into journal ( release ) values ( @release )",
			[ new JournalingParameter( "release", "r1" ) ]
		);

		transaction.Commit();

		Assert.Equal( [ "r1" ], SqlServerDatabase.Query( connection, "select release from journal" ) );
	}

	/// <summary>
	/// Every parameter a slot carries is supplied whether the statement mentions it or not; SqlClient
	/// declares them all to sp_executesql, which accepts parameters the batch never uses.
	/// </summary>
	[DockerAmd64Fact]
	public void AParameterTheStatementNeverMentionsIsIgnoredRatherThanRefused()
	{
		using IDatabaseDriver driver = _server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table journal ( release nvarchar(128) )" );

		transaction.ExecuteJournalingStatement(
			"insert into journal ( release ) values ( @release )",
			[
				new JournalingParameter( "release", "r1" ),
				new JournalingParameter( "description", "never mentioned" ),
				new JournalingParameter( "utcTimestamp", DateTime.UtcNow )
			]
		);

		transaction.Commit();

		Assert.Equal( [ "r1" ], SqlServerDatabase.Query( connection, "select release from journal" ) );
	}

	/// <summary>
	/// Why the driver binds a <see cref="DateTime"/> as 'datetime2': SqlClient's default is
	/// 'datetime', which rounds to a third of a millisecond before the value reaches the column, so
	/// the instant stored would not be the host's.
	/// </summary>
	[DockerAmd64Fact]
	public void AUtcTimestampReachesADatetime2ColumnToTheTick()
	{
		DateTime instant = new DateTime( 2026, 10, 1, 12, 34, 56, DateTimeKind.Utc ).AddTicks( 1234567 );

		using IDatabaseDriver driver = _server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table journal ( at_utc datetime2 )" );

		transaction.ExecuteJournalingStatement(
			"insert into journal ( at_utc ) values ( @utcTimestamp )",
			[ new JournalingParameter( "utcTimestamp", instant ) ]
		);

		transaction.Commit();

		Assert.Equal(
			[ "1" ],
			SqlServerDatabase.Query(
				connection,
				"select count( * ) from journal where at_utc = '2026-10-01T12:34:56.1234567'"
			)
		);
	}

	[DockerAmd64Fact]
	public void AJournalingQueryReturnsAReaderTheCallerDisposes()
	{
		using IDatabaseDriver driver = _server.Driver( out string _ );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript(
			"""
			create table journal ( release nvarchar(128), completed bit );
			insert into journal ( release, completed ) values ( 'r1', 1 );
			"""
		);

		using( IDataReader reader = transaction.ExecuteJournalingQuery(
				"select release, completed from journal where release = @release",
				[ new JournalingParameter( "release", "r1" ) ]
			) )
		{
			Assert.True( reader.Read() );
			Assert.Equal( "r1", reader.GetValue( 0 ) );
			Assert.Equal( true, reader.GetValue( 1 ) );
			Assert.False( reader.Read() );
		}

		transaction.Commit();
	}

	// ------------------------------------------------------------------ GO splitting

	[Fact]
	public void GoAloneOnALineInAnyCaseSeparatesBatches()
	{
		Assert.Equal(
			[ "create table a ( id int )", "create table b ( id int )\r\nselect 1".ReplaceLineEndings( "\n" ) ],
			SqlServerBatches.Split( "create table a ( id int )\r\n  go  \r\ncreate table b ( id int )\r\nselect 1\r\nGO\r\n" )
		);
	}

	[Fact]
	public void GoInsideALineOrWithACountIsNotASeparator()
	{
		const string script = "select 'GO' as word\nselect 1 -- GO\nGO 2";

		Assert.Equal( [ script ], SqlServerBatches.Split( script ) );
	}

	[Fact]
	public void SeparatorsAloneLeaveNothingToRun() => Assert.Empty( SqlServerBatches.Split( "GO\n  \ngo\n" ) );

	[Fact]
	public void AScriptWithoutGoIsOneBatchAsWritten()
	{
		const string script = "create table a ( id int );\r\ninsert into a values ( 1 );";

		Assert.Equal( [ script ], SqlServerBatches.Split( script ) );
	}

	// ------------------------------------------------------------------ the factory

	[Fact]
	public void TheDriverIsNamedSqlServerAndTakesEitherTheParametersOrAConnectionString()
	{
		SqlServerDatabaseDriverFactory factory = new();

		Assert.Equal( "sqlserver", factory.Name );

		DatabaseDriverParametersDefinition definition = factory.GetParametersDefinition();

		Assert.Equal(
			[
				"host", "port", "database", "username", "password", "password-env", "trust-server-certificate",
				"connection-string"
			],
			definition.Parameters.Select( parameter => parameter.Name )
		);

		Assert.Equal( [ [ "host", "database" ], [ "connection-string" ] ], definition.RequiredParametersOptions );
	}

	[Fact]
	public void AConnectionStringAlongsideTheIndividualParametersIsRefused()
	{
		ArgumentException error = Assert.Throws<ArgumentException>( () => Create(
				[ "connection-string", "Server=a;Database=b" ],
				[ "trust-server-certificate", "true" ]
			)
		);

		Assert.Contains( "one or the other", error.Message );
	}

	[Fact]
	public void HostAndDatabaseAreBothRequired()
	{
		Assert.Contains( "'host' is required", Assert.Throws<ArgumentException>( () => Create() ).Message );

		Assert.Contains(
			"'database' is required",
			Assert.Throws<ArgumentException>( () => Create( [ "host", "a" ] ) ).Message
		);
	}

	[Fact]
	public void ANonNumericPortOrTrustValueIsRefused()
	{
		Assert.Contains(
			"must be a number",
			Assert.Throws<ArgumentException>( () => Create( [ "host", "a" ], [ "database", "b" ], [ "port", "x" ] ) )
				.Message
		);

		Assert.Contains(
			"must be 'true' or 'false'",
			Assert.Throws<ArgumentException>(
					() => Create( [ "host", "a" ], [ "database", "b" ], [ "trust-server-certificate", "yes" ] ) )
				.Message
		);
	}

	/// <summary>
	/// Without a login, SQL Server authenticates the process's own identity; a password with no
	/// login to go with it is a mistake rather than something to drop silently.
	/// </summary>
	[Fact]
	public void APasswordNeedsAUsername()
	{
		Assert.Contains(
			"need 'username'",
			Assert.Throws<ArgumentException>( () => Create( [ "host", "a" ], [ "database", "b" ], [ "password", "p" ] ) )
				.Message
		);

		Create( [ "host", "a" ], [ "database", "b" ] ).Dispose();
		Create( [ "host", "a" ], [ "database", "b" ], [ "username", "sa" ], [ "password", "p" ] ).Dispose();
	}

	/// <summary>
	/// Windows builds leave SqlClient's native networking library out, so the driver must have
	/// switched SqlClient to its managed networking before any connection is made.
	/// </summary>
	[Fact]
	public void TheDriverSwitchesSqlClientToManagedNetworking()
	{
		_ = new SqlServerDatabaseDriverFactory();

		Assert.True( AppContext.TryGetSwitch( SqlServerDatabaseDriverFactory.ManagedNetworkingSwitch, out bool enabled ) );
		Assert.True( enabled );
	}

	[Fact]
	public void TheRegistryExtensionRegistersTheDriverUnderItsName()
	{
		DatabaseDriverRegistry registry = new();

		registry.RegisterSqlServerDriver();

		Assert.Equal( [ "sqlserver" ], registry.Names );
		Assert.IsType<SqlServerDatabaseDriverFactory>( registry.Create( "sqlserver" ) );
	}

	private static IDatabaseDriver Create( params string[][] parameters )
		=> new SqlServerDatabaseDriverFactory().Create(
			parameters.ToDictionary( pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase ),
			TimeSpan.FromSeconds( 30 )
		);
}
