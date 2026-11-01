using System.Data;
using Pug.Sqetch.Deployment;
using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.DatabaseDrivers.PostgreSql;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// The driver against a real PostgreSQL server in a container. The parameter-binding tests are the
/// ones that could not be written any other way: whether Npgsql refuses a parameter the statement
/// never mentions decides what the driver's dialect has to do, and only Npgsql can answer it.
/// </summary>
public class PostgreSqlDriverTests : IClassFixture<PostgresDatabase>
{
	private readonly PostgresDatabase _server;

	public PostgreSqlDriverTests( PostgresDatabase server ) => _server = server;

	[DockerFact]
	public void ACommittedTransactionsWorkSurvivesIncludingItsDdl()
	{
		using IDatabaseDriver driver = _server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table thing ( id integer primary key )" );
		transaction.ExecuteStepScript( "insert into thing ( id ) values ( 1 )" );
		transaction.Commit();

		Assert.Equal( ["1"], PostgresDatabase.Query( connection, "select id from thing" ) );
	}

	/// <summary>
	/// PostgreSQL rolls DDL back with everything else, so a failed release at release commit level
	/// leaves no half-created schema here.
	/// </summary>
	[DockerFact]
	public void ARolledBackTransactionLeavesNoTableBehind()
	{
		using IDatabaseDriver driver = _server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table thing ( id integer primary key )" );
		transaction.Rollback();

		Assert.Empty(
			PostgresDatabase.Query( connection, "select tablename from pg_tables where tablename = 'thing'" ) );
	}

	[DockerFact]
	public void DisposingTheDriverRollsBackAnOpenTransaction()
	{
		string connection;

		using( IDatabaseDriver driver = _server.Driver( out connection ) )
		{
			driver.BeginTransaction().ExecuteStepScript( "create table thing ( id integer primary key )" );
		}

		Assert.Empty(
			PostgresDatabase.Query( connection, "select tablename from pg_tables where tablename = 'thing'" ) );
	}

	[DockerFact]
	public void TheConnectionIsReusedSoOneTransactionFollowsAnother()
	{
		using IDatabaseDriver driver = _server.Driver( out string connection );

		IDatabaseTransaction first = driver.BeginTransaction();

		first.ExecuteStepScript( "create table thing ( id integer primary key )" );
		first.Commit();

		IDatabaseTransaction second = driver.BeginTransaction();

		second.ExecuteStepScript( "insert into thing ( id ) values ( 2 )" );
		second.Commit();

		Assert.Equal( ["2"], PostgresDatabase.Query( connection, "select id from thing" ) );
	}

	[DockerFact]
	public void AStepScriptMayHoldSeveralStatements()
	{
		using IDatabaseDriver driver = _server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript(
			"""
			create table thing ( id integer primary key, name text );
			insert into thing ( id, name ) values ( 1, 'a' );
			insert into thing ( id, name ) values ( 2, 'b' );
			""" );

		transaction.Commit();

		Assert.Equal(
			["1|a", "2|b"], PostgresDatabase.Query( connection, "select id, name from thing order by id" ) );
	}

	// ------------------------------------------------------------------ journaling parameters

	/// <summary>
	/// Sqetch supplies bare names and leaves the prefix to the driver; this pins which placeholder
	/// forms Npgsql resolves them against.
	/// </summary>
	[DockerTheory]
	[InlineData( "@release" )]
	[InlineData( ":release" )]
	public void APrefixlessParameterNameBindsToThePlaceholderFormsNpgsqlAccepts( string placeholder )
	{
		using IDatabaseDriver driver = _server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table journal ( release text )" );
		transaction.ExecuteJournalingStatement(
			$"insert into journal ( release ) values ( {placeholder} )", [new JournalingParameter( "release", "r1" )] );

		transaction.Commit();

		Assert.Equal( ["r1"], PostgresDatabase.Query( connection, "select release from journal" ) );
	}

	/// <summary>
	/// Every parameter a slot carries is supplied whether the maintainer's statement mentions it or
	/// not, so what Npgsql does with the unmentioned ones decides whether the driver has to scan the
	/// SQL before binding. It ignores them — it rewrites named placeholders to positional ones, but
	/// only for the names it actually found — so the driver binds them all and no scan exists.
	/// </summary>
	[DockerFact]
	public void AParameterTheStatementNeverMentionsIsIgnoredRatherThanRefused()
	{
		using IDatabaseDriver driver = _server.Driver( out string connection );

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

		Assert.Equal( ["r1"], PostgresDatabase.Query( connection, "select release from journal" ) );
	}

	/// <summary>
	/// The point of binding the instant as a <see cref="DateTime"/> rather than as text: it reaches a
	/// 'timestamptz' column as the instant it is, with no cast in the maintainer's SQL and no
	/// formatting for a parser to disagree with.
	/// </summary>
	[DockerFact]
	public void AUtcTimestampReachesATimestamptzColumnAsTheSameInstant()
	{
		DateTime instant = new ( 2026, 10, 1, 12, 34, 56, DateTimeKind.Utc );

		using IDatabaseDriver driver = _server.Driver( out string connection );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript( "create table journal ( at_utc timestamptz )" );
		transaction.ExecuteJournalingStatement(
			"insert into journal ( at_utc ) values ( @utcTimestamp )",
			[new JournalingParameter( "utcTimestamp", instant )] );

		transaction.Commit();

		Assert.Equal(
			["1"],
			PostgresDatabase.Query(
				connection, $"select count( * ) from journal where at_utc = '{instant:yyyy-MM-dd HH:mm:ss}+00'" ) );
	}

	[DockerFact]
	public void AJournalingQueryReturnsAReaderTheCallerDisposes()
	{
		using IDatabaseDriver driver = _server.Driver( out string _ );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		transaction.ExecuteStepScript(
			"""
			create table journal ( release text, completed boolean );
			insert into journal ( release, completed ) values ( 'r1', true );
			""" );

		using( IDataReader reader = transaction.ExecuteJournalingQuery(
					"select release, completed from journal where release = @release",
					[new JournalingParameter( "release", "r1" )] ) )
		{
			Assert.True( reader.Read() );
			Assert.Equal( "r1", reader.GetValue( 0 ) );
			Assert.Equal( true, reader.GetValue( 1 ) );
			Assert.False( reader.Read() );
		}

		transaction.Commit();
	}

	// ------------------------------------------------------------------ the factory

	[Fact]
	public void TheDriverIsNamedPostgresAndTakesEitherTheParametersOrAConnectionString()
	{
		PostgreSqlDatabaseDriverFactory factory = new ();

		Assert.Equal( "postgres", factory.Name );

		DatabaseDriverParametersDefinition definition = factory.GetParametersDefinition();

		Assert.Equal(
			["host", "port", "database", "username", "password", "password-env", "connection-string"],
			definition.Parameters.Select( parameter => parameter.Name ) );

		Assert.Equal( [["host", "database"], ["connection-string"]], definition.RequiredParametersOptions );
	}

	[Fact]
	public void AConnectionStringAlongsideTheIndividualParametersIsRefused()
	{
		ArgumentException error = Assert.Throws<ArgumentException>(
			() => Create( ["connection-string", "Host=a;Database=b"], ["host", "c"] ) );

		Assert.Contains( "one or the other", error.Message );
	}

	[Fact]
	public void HostAndDatabaseAreBothRequired()
	{
		Assert.Contains( "'host' is required", Assert.Throws<ArgumentException>( () => Create() ).Message );
		Assert.Contains(
			"'database' is required", Assert.Throws<ArgumentException>( () => Create( ["host", "a"] ) ).Message );
	}

	[Fact]
	public void ANonNumericPortIsRefused()
	{
		ArgumentException error = Assert.Throws<ArgumentException>(
			() => Create( ["host", "a"], ["database", "b"], ["port", "fifty"] ) );

		Assert.Contains( "must be a number", error.Message );
	}

	/// <summary>
	/// A command line is readable by every process on the host, so the password is better named than
	/// spelled; naming a variable that is not set is a mistake worth reporting rather than connecting
	/// without one.
	/// </summary>
	[Fact]
	public void APasswordMayBeNamedByEnvironmentVariableInsteadOfSpelledOut()
	{
		string variable = $"SQETCH_TEST_{Guid.NewGuid():N}";

		Environment.SetEnvironmentVariable( variable, "s3cret" );

		try
		{
			IDatabaseDriver driver = Create( ["host", "a"], ["database", "b"], ["password-env", variable] );

			driver.Dispose();
		}
		finally
		{
			Environment.SetEnvironmentVariable( variable, null );
		}

		ArgumentException unset = Assert.Throws<ArgumentException>(
			() => Create( ["host", "a"], ["database", "b"], ["password-env", variable] ) );

		Assert.Contains( "is not set", unset.Message );

		ArgumentException both = Assert.Throws<ArgumentException>(
			() => Create( ["host", "a"], ["database", "b"], ["password", "p"], ["password-env", variable] ) );

		Assert.Contains( "not both", both.Message );
	}

	[Fact]
	public void TheRegistryExtensionRegistersTheDriverUnderItsName()
	{
		DatabaseDriverRegistry registry = new ();

		registry.RegisterPostgreSqlDriver();

		Assert.Equal( ["postgres"], registry.Names );
		Assert.IsType<PostgreSqlDatabaseDriverFactory>( registry.Create( "postgres" ) );
	}

	private static IDatabaseDriver Create( params string[][] parameters )
		=> new PostgreSqlDatabaseDriverFactory().Create(
			parameters.ToDictionary( pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase ),
			TimeSpan.FromSeconds( 30 ) );
}
