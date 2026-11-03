using System.Data;

using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.DatabaseDrivers.MySql;
using Pug.Sqetch.Shared;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// The 'mysql' driver against a real server, run once against MySQL and once against MariaDB. Pins
/// what sets this family apart: DDL commits implicitly, so a rolled-back transaction keeps the tables
/// it created, and the driver's own connection settings let a step script use user variables.
/// </summary>
public abstract class MySqlFamilyDriverTests( MySqlFamilyServer server )
{
	private const string TableExists =
		"select table_name from information_schema.tables where table_schema = database() and table_name = 'thing'";

	[DockerFact]
	public void ACommittedTransactionsWorkSurvivesIncludingItsDdl()
	{
		using IDatabaseDriver driver = server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table thing ( id integer primary key )" );
		transaction.ExecuteStepScript( "insert into thing ( id ) values ( 1 )" );
		transaction.Commit();

		Assert.Equal( [ "1" ], MySqlFamilyServer.Query( connection, "select id from thing" ) );
	}

	/// <summary>
	/// Unlike every other engine Sqetch supports, DDL commits the open transaction: rolling back does
	/// not take the table away. This is why a release is not all-or-nothing on MySQL and MariaDB.
	/// </summary>
	[DockerFact]
	public void DdlCommitsImplicitlySoARollbackLeavesTheTable()
	{
		using IDatabaseDriver driver = server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table thing ( id integer primary key )" );
		transaction.Rollback();

		Assert.Equal( [ "thing" ], MySqlFamilyServer.Query( connection, TableExists ) );
	}

	[DockerFact]
	public void ARolledBackTransactionLeavesNoRowsBehind()
	{
		using IDatabaseDriver driver = server.Driver( out string connection );

		IDatabaseTransaction create = driver.BeginTransaction();

		create.ExecuteStepScript( "create table thing ( id integer primary key )" );
		create.Commit();

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "insert into thing ( id ) values ( 1 )" );
		transaction.Rollback();

		Assert.Empty( MySqlFamilyServer.Query( connection, "select id from thing" ) );
	}

	[DockerFact]
	public void DisposingTheDriverRollsBackAnOpenTransaction()
	{
		string connection;

		using( IDatabaseDriver driver = server.Driver( out connection ) )
		{
			IDatabaseTransaction create = driver.BeginTransaction();

			create.ExecuteStepScript( "create table thing ( id integer primary key )" );
			create.Commit();

			driver.BeginTransaction().ExecuteStepScript( "insert into thing ( id ) values ( 1 )" );
		}

		Assert.Empty( MySqlFamilyServer.Query( connection, "select id from thing" ) );
	}

	[DockerFact]
	public void TheConnectionIsReusedSoOneTransactionFollowsAnother()
	{
		using IDatabaseDriver driver = server.Driver( out string connection );

		IDatabaseTransaction first = driver.BeginTransaction();

		first.ExecuteStepScript( "create table thing ( id integer primary key )" );
		first.Commit();

		IDatabaseTransaction second = driver.BeginTransaction();

		second.ExecuteStepScript( "insert into thing ( id ) values ( 2 )" );
		second.Commit();

		Assert.Equal( [ "2" ], MySqlFamilyServer.Query( connection, "select id from thing" ) );
	}

	[DockerFact]
	public void AStepScriptMayHoldSeveralStatements()
	{
		using IDatabaseDriver driver = server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript(
			"""
			create table thing ( id integer primary key, name varchar(10) );
			insert into thing ( id, name ) values ( 1, 'a' );
			insert into thing ( id, name ) values ( 2, 'b' );
			"""
		);

		transaction.Commit();

		Assert.Equal(
			[ "1|a", "2|b" ],
			MySqlFamilyServer.Query( connection, "select id, name from thing order by id" )
		);
	}

	/// <summary>
	/// MySqlConnector takes an undeclared '@name' for a missing parameter unless user variables are
	/// allowed, which the driver does in the connection strings it builds.
	/// </summary>
	[DockerFact]
	public void AStepScriptMayUseUserVariables()
	{
		string connection = server.CreateDatabase( out string database );

		using IDatabaseDriver driver = server.DriverFromParameters( database );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript(
			"""
			create table thing ( id integer primary key );
			set @next = 41 + 1;
			insert into thing ( id ) values ( @next );
			"""
		);

		transaction.Commit();

		Assert.Equal( [ "42" ], MySqlFamilyServer.Query( connection, "select id from thing" ) );
	}

	// ------------------------------------------------------------------ journaling parameters

	/// <summary>MySqlConnector resolves a bare name against both placeholder forms it accepts.</summary>
	[DockerTheory]
	[InlineData( "@name" )]
	[InlineData( "?name" )]
	public void APrefixlessParameterNameBindsToThePlaceholderFormsMySqlConnectorAccepts( string placeholder )
	{
		using IDatabaseDriver driver = server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table journal ( name varchar(128) )" );

		transaction.ExecuteJournalingStatement(
			$"insert into journal ( name ) values ( {placeholder} )",
			[ new JournalingParameter( "name", "r1" ) ]
		);

		transaction.Commit();

		Assert.Equal( [ "r1" ], MySqlFamilyServer.Query( connection, "select name from journal" ) );
	}

	[DockerFact]
	public void AParameterTheStatementNeverMentionsIsIgnoredRatherThanRefused()
	{
		using IDatabaseDriver driver = server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table journal ( `release` varchar(128) )" );

		transaction.ExecuteJournalingStatement(
			"insert into journal ( `release` ) values ( @release )",
			[
				new JournalingParameter( "release", "r1" ),
				new JournalingParameter( "description", "never mentioned" ),
				new JournalingParameter( "utcTimestamp", DateTime.UtcNow )
			]
		);

		transaction.Commit();

		Assert.Equal( [ "r1" ], MySqlFamilyServer.Query( connection, "select `release` from journal" ) );
	}

	/// <summary>The instant reaches a 'datetime(6)' column as written, to the microsecond, unconverted.</summary>
	[DockerFact]
	public void AUtcTimestampReachesADatetimeColumnToTheMicrosecond()
	{
		DateTime instant = new DateTime( 2026, 10, 1, 12, 34, 56, DateTimeKind.Utc ).AddTicks( 1234560 );

		using IDatabaseDriver driver = server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table journal ( at_utc datetime(6) )" );

		transaction.ExecuteJournalingStatement(
			"insert into journal ( at_utc ) values ( @utcTimestamp )",
			[ new JournalingParameter( "utcTimestamp", instant ) ]
		);

		transaction.Commit();

		Assert.Equal(
			[ "1" ],
			MySqlFamilyServer.Query(
				connection,
				"select count( * ) from journal where at_utc = '2026-10-01 12:34:56.123456'"
			)
		);
	}

	[DockerFact]
	public void AJournalingQueryReturnsAReaderTheCallerDisposes()
	{
		using IDatabaseDriver driver = server.Driver( out string _ );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript(
			"""
			create table journal ( name varchar(128), completed boolean );
			insert into journal ( name, completed ) values ( 'r1', true );
			"""
		);

		using( IDataReader reader = transaction.ExecuteJournalingQuery(
				"select name, completed from journal where name = @name",
				[ new JournalingParameter( "name", "r1" ) ]
			) )
		{
			Assert.True( reader.Read() );
			Assert.Equal( "r1", reader.GetValue( 0 ) );
			Assert.Equal( true, reader.GetValue( 1 ) );
			Assert.False( reader.Read() );
		}

		transaction.Commit();
	}
}

public sealed class MySqlDriverTests( MySqlServer server ) : MySqlFamilyDriverTests( server ), IClassFixture<MySqlServer>;

public sealed class MariaDbDriverTests( MariaDbServer server ) : MySqlFamilyDriverTests( server ), IClassFixture<MariaDbServer>;

/// <summary>The factory's parameter rules, which need no server.</summary>
public class MySqlDriverFactoryTests
{
	[Fact]
	public void TheDriverIsNamedMySqlAndTakesEitherTheParametersOrAConnectionString()
	{
		MySqlDatabaseDriverFactory factory = new();

		Assert.Equal( "mysql", factory.Name );

		DatabaseDriverParametersDefinition definition = factory.GetParametersDefinition();

		Assert.Equal(
			[ "host", "port", "database", "username", "password", "password-env", "connection-string" ],
			definition.Parameters.Select( parameter => parameter.Name )
		);

		Assert.Equal( [ [ "host", "database" ], [ "connection-string" ] ], definition.RequiredParametersOptions );
	}

	[Fact]
	public void AConnectionStringAlongsideTheIndividualParametersIsRefused()
		=> Assert.Contains(
			"one or the other",
			Assert.Throws<ArgumentException>(
					() => Create( [ "connection-string", "Server=a;Database=b" ], [ "host", "c" ] ) )
				.Message
		);

	[Fact]
	public void HostAndDatabaseAreBothRequiredAndThePortIsANumber()
	{
		Assert.Contains( "'host' is required", Assert.Throws<ArgumentException>( () => Create() ).Message );

		Assert.Contains(
			"'database' is required",
			Assert.Throws<ArgumentException>( () => Create( [ "host", "a" ] ) ).Message
		);

		Assert.Contains(
			"must be a number",
			Assert.Throws<ArgumentException>( () => Create( [ "host", "a" ], [ "database", "b" ], [ "port", "x" ] ) )
				.Message
		);
	}

	/// <summary>The flag the engine's failure handling turns on: MySQL and MariaDB commit DDL by themselves.</summary>
	[Fact]
	public void TheDriverSaysItsEngineCommitsDdlImplicitly()
	{
		using IDatabaseDriver driver = Create( [ "host", "a" ], [ "database", "b" ] );

		Assert.True( driver.CommitsDdlImplicitly );
	}

	[Fact]
	public void TheRegistryExtensionRegistersTheDriverUnderItsName()
	{
		DatabaseDriverRegistry registry = new();

		registry.RegisterMySqlDriver();

		Assert.Equal( [ "mysql" ], registry.Names );
		Assert.IsType<MySqlDatabaseDriverFactory>( registry.Create( "mysql" ) );
	}

	private static IDatabaseDriver Create( params string[][] parameters )
		=> new MySqlDatabaseDriverFactory().Create(
			parameters.ToDictionary( pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase ),
			TimeSpan.FromSeconds( 30 )
		);
}
