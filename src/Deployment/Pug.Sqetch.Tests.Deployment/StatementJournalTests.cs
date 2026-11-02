using System.Data;

using Pug.Sqetch.Deployment;

// JournalingSlot, JournalingSlots and JournalingStatements live in Pug.Sqetch.Planning.Models, namespace
// Pug.Sqetch.
using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Tests.Deployment;

/// <summary>
/// <see cref="StatementJournal"/> is the only place the fifteen slots' parameter contracts and the
/// two query result contracts are exercised directly, rather than through a full deployment.
/// </summary>
public class StatementJournalTests
{
	private const string Project = "proj";

	/// <summary>A fixed clock, so the 'utcTimestamp' parameter can be asserted exactly.</summary>
	private static readonly DateTime Instant = new( 2026, 10, 1, 12, 34, 56, 789, DateTimeKind.Utc );

	[Fact]
	public void EveryEventAndPrepareSlotIsCalledWithExactlyItsParameters()
	{
		( FakeTransaction transaction, StatementJournal journal ) = Setup();

		journal.DeployingRelease( "r1", "d1", transaction );

		AssertCall(
			transaction,
			0,
			"-- DeployingRelease",
			( "project", Project ),
			( "release", "r1" ),
			( "description", "d1" ),
			( "utcTimestamp", Instant )
		);

		journal.DeployingPlan( "r1", "p1", "d2", transaction );

		AssertCall(
			transaction,
			1,
			"-- DeployingPlan",
			( "project", Project ),
			( "release", "r1" ),
			( "plan", "p1" ),
			( "description", "d2" ),
			( "utcTimestamp", Instant )
		);

		journal.DeployingStep( "r1", "p1", "s1", "d3", transaction );

		AssertCall(
			transaction,
			2,
			"-- DeployingStep",
			( "project", Project ),
			( "release", "r1" ),
			( "plan", "p1" ),
			( "step", "s1" ),
			( "description", "d3" ),
			( "utcTimestamp", Instant )
		);

		journal.StepDeployed( "r1", "p1", "s1", "d4", transaction );

		AssertCall(
			transaction,
			3,
			"-- StepDeployed",
			( "project", Project ),
			( "release", "r1" ),
			( "plan", "p1" ),
			( "step", "s1" ),
			( "description", "d4" ),
			( "utcTimestamp", Instant )
		);

		journal.PlanDeployed( "r1", "p1", "d5", transaction );

		AssertCall(
			transaction,
			4,
			"-- PlanDeployed",
			( "project", Project ),
			( "release", "r1" ),
			( "plan", "p1" ),
			( "description", "d5" ),
			( "utcTimestamp", Instant )
		);

		journal.ReleaseDeployed( "r1", "d6", transaction );

		AssertCall(
			transaction,
			5,
			"-- ReleaseDeployed",
			( "project", Project ),
			( "release", "r1" ),
			( "description", "d6" ),
			( "utcTimestamp", Instant )
		);

		journal.RollingBackRelease( "r1", transaction );

		AssertCall(
			transaction,
			6,
			"-- RollingBackRelease",
			( "project", Project ),
			( "release", "r1" ),
			( "utcTimestamp", Instant )
		);

		journal.RollingBackPlan( "r1", "p1", transaction );

		AssertCall(
			transaction,
			7,
			"-- RollingBackPlan",
			( "project", Project ),
			( "release", "r1" ),
			( "plan", "p1" ),
			( "utcTimestamp", Instant )
		);

		journal.RollingBackStep( "r1", "p1", "s1", transaction );

		AssertCall(
			transaction,
			8,
			"-- RollingBackStep",
			( "project", Project ),
			( "release", "r1" ),
			( "plan", "p1" ),
			( "step", "s1" ),
			( "utcTimestamp", Instant )
		);

		journal.RolledBackStep( "r1", "p1", "s1", transaction );

		AssertCall(
			transaction,
			9,
			"-- RolledBackStep",
			( "project", Project ),
			( "release", "r1" ),
			( "plan", "p1" ),
			( "step", "s1" ),
			( "utcTimestamp", Instant )
		);

		journal.RolledBackPlan( "r1", "p1", transaction );

		AssertCall(
			transaction,
			10,
			"-- RolledBackPlan",
			( "project", Project ),
			( "release", "r1" ),
			( "plan", "p1" ),
			( "utcTimestamp", Instant )
		);

		journal.RolledBackRelease( "r1", transaction );

		AssertCall(
			transaction,
			11,
			"-- RolledBackRelease",
			( "project", Project ),
			( "release", "r1" ),
			( "utcTimestamp", Instant )
		);

		journal.PrepareJournal( transaction );
		AssertCall( transaction, 12, "-- PrepareJournal", ( "project", Project ) );

		Assert.Equal( 13, transaction.JournalingCalls.Count );
	}

	/// <summary>
	/// A slot may hold several statements; they record one event, so they are given one instant
	/// rather than each reading the clock again.
	/// </summary>
	[Fact]
	public void TheClockIsReadOncePerEventNotOncePerStatement()
	{
		Dictionary<JournalingSlot, string?> text = Complete();
		text[JournalingSlot.DeployingPlan] = "first\n;;\nsecond\n;;\nthird";

		int reads = 0;
		FakeDatabaseDriver driver = new();
		FakeTransaction transaction = new( driver, 1 );

		StatementJournal journal = new(
			new JournalingStatements( text ),
			Project,
			() => Instant.AddSeconds( reads++ )
		);

		journal.DeployingPlan( "r1", "p1", "d1", transaction );

		Assert.Equal( 1, reads );
		Assert.Equal( 3, transaction.JournalingCalls.Count );

		Assert.All(
			transaction.JournalingCalls,
			call => Assert.Equal(
				Instant,
				Assert.Single( call.Parameters, parameter => parameter.Name == "utcTimestamp" ).Value
			)
		);
	}

	/// <summary>
	/// A driver binding an unspecified-kind <see cref="DateTime"/> may convert it as local time, so
	/// the kind is forced rather than trusted — the parameter's whole contract is that it is UTC.
	/// </summary>
	[Theory]
	[InlineData( DateTimeKind.Unspecified )]
	[InlineData( DateTimeKind.Local )]
	public void AClockReadingThatIsNotAlreadyUtcIsBoundAsUtc( DateTimeKind kind )
	{
		DateTime reading = DateTime.SpecifyKind( Instant, kind );

		FakeDatabaseDriver driver = new();
		FakeTransaction transaction = new( driver, 1 );
		StatementJournal journal = new( new JournalingStatements( Complete() ), Project, () => reading );

		journal.ReleaseDeployed( "r1", "d1", transaction );

		DateTime bound = Assert.IsType<DateTime>(
			Assert.Single(
					transaction.JournalingCalls[0].Parameters,
					parameter => parameter.Name == "utcTimestamp"
				)
				.Value
		);

		Assert.Equal( DateTimeKind.Utc, bound.Kind );
		Assert.Equal( kind == DateTimeKind.Local ? reading.ToUniversalTime() : Instant, bound );
	}

	[Fact]
	public void GetLatestReleaseQueriesWithOnlyTheProject()
	{
		( FakeTransaction transaction, StatementJournal journal ) = Setup();
		SeedLatestRelease( transaction.Driver, rows: [ ] );

		journal.GetLatestRelease( transaction );

		AssertCall( transaction, 0, "-- GetLatestRelease", ( "project", Project ) );
	}

	[Fact]
	public void GetDeployedPlansQueriesWithProjectAndRelease()
	{
		( FakeTransaction transaction, StatementJournal journal ) = Setup();
		SeedDeployedPlans( transaction.Driver, rows: [ ] );

		journal.GetDeployedPlans( "r1", transaction );

		AssertCall( transaction, 0, "-- GetDeployedPlans", ( "project", Project ), ( "release", "r1" ) );
	}

	[Fact]
	public void MultipleStatementsInASlotExecuteInOrder()
	{
		Dictionary<JournalingSlot, string?> text = Complete();
		text[JournalingSlot.DeployingRelease] = "first\n;;\nsecond\n;;\nthird";

		( FakeTransaction transaction, StatementJournal journal ) = Setup( text );

		journal.DeployingRelease( "r1", "d1", transaction );

		Assert.Equal( [ "first", "second", "third" ], transaction.JournalingCalls.Select( call => call.Statement ) );

		Assert.All(
			transaction.JournalingCalls,
			call => Assert.Equal(
				new JournalingParameter[]
				{
					new( "project", Project ), new( "release", "r1" ), new( "description", "d1" ),
					new( "utcTimestamp", Instant )
				},
				call.Parameters
			)
		);
	}

	[Fact]
	public void AFailingStatementIsWrappedWithItsOneBasedPositionWithinTheSlot()
	{
		Dictionary<JournalingSlot, string?> text = Complete();
		text[JournalingSlot.DeployingRelease] = "first\n;;\nsecond\n;;\nthird";

		( FakeTransaction transaction, StatementJournal journal ) = Setup( text );
		transaction.Driver.FailingJournalingStatements.Add( "second" );

		JournalingStatementException error =
			Assert.Throws<JournalingStatementException>( () => journal.DeployingRelease( "r1", "d1", transaction ) );

		Assert.Equal( "DeployingRelease", error.Slot );
		Assert.Equal( 2, error.Statement );
		Assert.IsType<InvalidOperationException>( error.InnerException );

		// the first statement already ran; only the failing one and anything after it did not
		Assert.Equal( [ "first", "second" ], transaction.JournalingCalls.Select( call => call.Statement ) );
	}

	[Fact]
	public void PrepareJournalFailureSaysPrepareMustBeSafeToRunAgain()
	{
		( FakeTransaction transaction, StatementJournal journal ) = Setup();
		transaction.Driver.FailingJournalingStatements.Add( "-- PrepareJournal" );

		JournalingStatementException error =
			Assert.Throws<JournalingStatementException>( () => journal.PrepareJournal( transaction ) );

		Assert.Equal( "PrepareJournal", error.Slot );
		Assert.Equal( 1, error.Statement );
		Assert.Contains( "safe to run again", error.Message );
	}

	[Fact]
	public void AFailingQueryIsWrappedAtStatementOne()
	{
		( FakeTransaction transaction, StatementJournal journal ) = Setup();
		transaction.Driver.FailingJournalingQueries.Add( "-- GetLatestRelease" );

		JournalingStatementException error =
			Assert.Throws<JournalingStatementException>( () => journal.GetLatestRelease( transaction ) );

		Assert.Equal( "GetLatestRelease", error.Slot );
		Assert.Equal( 1, error.Statement );
	}

	// ------------------------------------------------------------------ GetLatestRelease reading

	[Fact]
	public void GetLatestReleaseReadsByOrdinalIgnoringExtraColumnsAndNames()
	{
		( FakeTransaction transaction, StatementJournal journal ) = Setup();

		DataTable table = new();
		table.Columns.Add( "RELEASE_NAME", typeof(string) ); // Oracle-folded upper-case name
		table.Columns.Add( "COMPLETED_FLAG", typeof(bool) );
		table.Columns.Add( "EXTRA", typeof(string) ); // a maintainer is free to select more
		table.Rows.Add( "r1", true, "ignored" );

		transaction.Driver.QueryResults["-- GetLatestRelease"] = table;

		JournaledRelease? release = journal.GetLatestRelease( transaction );

		Assert.Equal( new JournaledRelease( "r1", true ), release );
	}

	[Fact]
	public void GetLatestReleaseWithNoRowsIsAFreshDatabase()
	{
		( FakeTransaction transaction, StatementJournal journal ) = Setup();
		SeedLatestRelease( transaction.Driver, rows: [ ] );

		Assert.Null( journal.GetLatestRelease( transaction ) );
	}

	[Fact]
	public void GetLatestReleaseWithMoreThanOneRowThrows()
	{
		( FakeTransaction transaction, StatementJournal journal ) = Setup();
		SeedLatestRelease( transaction.Driver, rows: [ ( "r1", true ), ( "r2", false ) ] );

		JournalingStatementException error =
			Assert.Throws<JournalingStatementException>( () => journal.GetLatestRelease( transaction ) );

		Assert.Equal( "GetLatestRelease", error.Slot );
		Assert.Equal( 1, error.Statement );
		Assert.Contains( "more than one row", error.Message );
	}

	[Fact]
	public void GetLatestReleaseWithANullReleaseNameThrowsRatherThanCoercingToThePseudoRelease()
	{
		( FakeTransaction transaction, StatementJournal journal ) = Setup();

		DataTable table = new();
		table.Columns.Add( "release", typeof(string) );
		table.Columns.Add( "completed", typeof(bool) );
		table.Rows.Add( DBNull.Value, true );

		transaction.Driver.QueryResults["-- GetLatestRelease"] = table;

		JournalingStatementException error =
			Assert.Throws<JournalingStatementException>( () => journal.GetLatestRelease( transaction ) );

		Assert.Equal( "GetLatestRelease", error.Slot );
		Assert.Contains( "NULL", error.Message );
	}

	[Fact]
	public void GetLatestReleaseWithFewerThanTwoColumnsThrows()
	{
		( FakeTransaction transaction, StatementJournal journal ) = Setup();

		DataTable table = new();
		table.Columns.Add( "release", typeof(string) );
		table.Rows.Add( "r1" );

		transaction.Driver.QueryResults["-- GetLatestRelease"] = table;

		JournalingStatementException error =
			Assert.Throws<JournalingStatementException>( () => journal.GetLatestRelease( transaction ) );

		Assert.Equal( "GetLatestRelease", error.Slot );
		Assert.Contains( "two columns", error.Message );
	}

	[Fact]
	public void GetLatestReleaseDisposesItsReader()
	{
		( FakeTransaction transaction, StatementJournal journal ) = Setup();
		SeedLatestRelease( transaction.Driver, rows: [ ] );

		journal.GetLatestRelease( transaction );

		Assert.True( ( (DataTableReader)Assert.Single( transaction.QueryReaders ) ).IsClosed );
	}

	// ------------------------------------------------------------------ GetDeployedPlans reading

	[Fact]
	public void GetDeployedPlansWithNoRowsIsEmpty()
	{
		( FakeTransaction transaction, StatementJournal journal ) = Setup();
		SeedDeployedPlans( transaction.Driver, rows: [ ] );

		Assert.Empty( journal.GetDeployedPlans( "r1", transaction ) );
	}

	[Fact]
	public void GetDeployedPlansReadsColumnZeroOfEveryRow()
	{
		( FakeTransaction transaction, StatementJournal journal ) = Setup();
		SeedDeployedPlans( transaction.Driver, rows: [ "a", "b", "c" ] );

		Assert.Equal( [ "a", "b", "c" ], journal.GetDeployedPlans( "r1", transaction ) );
	}

	[Fact]
	public void GetDeployedPlansWithANullPlanNameThrows()
	{
		( FakeTransaction transaction, StatementJournal journal ) = Setup();

		DataTable table = new();
		table.Columns.Add( "plan", typeof(string) );
		table.Rows.Add( DBNull.Value );

		transaction.Driver.QueryResults["-- GetDeployedPlans"] = table;

		JournalingStatementException error =
			Assert.Throws<JournalingStatementException>( () => journal.GetDeployedPlans( "r1", transaction ) );

		Assert.Equal( "GetDeployedPlans", error.Slot );
		Assert.Contains( "NULL", error.Message );
	}

	[Fact]
	public void GetDeployedPlansDisposesItsReader()
	{
		( FakeTransaction transaction, StatementJournal journal ) = Setup();
		SeedDeployedPlans( transaction.Driver, rows: [ ] );

		journal.GetDeployedPlans( "r1", transaction );

		Assert.True( ( (DataTableReader)Assert.Single( transaction.QueryReaders ) ).IsClosed );
	}

	// ------------------------------------------------------------------ 'completed' coercion

	/// <summary>
	/// <see cref="Convert.ToBoolean(object)"/> alone throws on 'Y' — exactly what an Oracle or DB2
	/// maintainer stores in a <c>CHAR(1)</c> — so the coercion is this explicit table instead.
	/// </summary>
	[Theory]
	[InlineData( true, true )]
	[InlineData( false, false )]
	[InlineData( 1, true )]
	[InlineData( 0, false )]
	[InlineData( -5, true )]
	[InlineData( 1.0, true )]
	[InlineData( 0.0, false )]
	[InlineData( "1", true )]
	[InlineData( "0", false )]
	[InlineData( "y", true )]
	[InlineData( "Y", true )]
	[InlineData( "yes", true )]
	[InlineData( "YES", true )]
	[InlineData( "t", true )]
	[InlineData( "T", true )]
	[InlineData( "true", true )]
	[InlineData( "TRUE", true )]
	[InlineData( "n", false )]
	[InlineData( "N", false )]
	[InlineData( "no", false )]
	[InlineData( "NO", false )]
	[InlineData( "f", false )]
	[InlineData( "F", false )]
	[InlineData( "false", false )]
	[InlineData( "FALSE", false )]
	[InlineData( 'y', true )]
	[InlineData( 'n', false )]
	public void CompletedCoercesByAnExplicitTable( object stored, bool expected )
		=> Assert.Equal( expected, ReadCompleted( stored ) );

	[Fact]
	public void CompletedNullThrows()
	{
		JournalingStatementException error =
			Assert.Throws<JournalingStatementException>( () => ReadCompleted( DBNull.Value ) );

		Assert.Contains( "NULL", error.Message );
	}

	[Fact]
	public void CompletedUnsupportedTypeThrows()
	{
		JournalingStatementException error =
			Assert.Throws<JournalingStatementException>( () => ReadCompleted( DateTime.UnixEpoch ) );

		Assert.Contains( "unsupported type", error.Message );
	}

	[Theory]
	[InlineData( "maybe" )]
	[InlineData( "yesno" )]
	[InlineData( "" )]
	public void CompletedUnrecognizedTextThrows( string text )
	{
		JournalingStatementException error = Assert.Throws<JournalingStatementException>( () => ReadCompleted( text ) );

		Assert.Contains( "unrecognized value", error.Message );
	}

	// ------------------------------------------------------------------ helpers

	private static (FakeTransaction Transaction, StatementJournal Journal) Setup(
		Dictionary<JournalingSlot, string?>? text = null
	)
	{
		FakeDatabaseDriver driver = new();
		FakeTransaction transaction = new( driver, 1 );
		StatementJournal journal = new( new JournalingStatements( text ?? Complete() ), Project, () => Instant );

		return ( transaction, journal );
	}

	private static void AssertCall(
		FakeTransaction transaction,
		int index,
		string statement,
		params (string Name, object Value)[] expected
	)
	{
		(string Statement, IReadOnlyList<JournalingParameter> Parameters) call = transaction.JournalingCalls[index];

		Assert.Equal( statement, call.Statement );
		Assert.Equal( expected.Select( p => new JournalingParameter( p.Name, p.Value ) ), call.Parameters );
	}

	private static void SeedLatestRelease(
		FakeDatabaseDriver driver,
		IReadOnlyList<(string Release, bool Completed)> rows
	)
	{
		DataTable table = new();
		table.Columns.Add( "release", typeof(string) );
		table.Columns.Add( "completed", typeof(bool) );

		foreach( ( string release, bool completed ) in rows )
			table.Rows.Add( release, completed );

		driver.QueryResults["-- GetLatestRelease"] = table;
	}

	private static void SeedDeployedPlans( FakeDatabaseDriver driver, IReadOnlyList<string> rows )
	{
		DataTable table = new();
		table.Columns.Add( "plan", typeof(string) );

		foreach( string plan in rows )
			table.Rows.Add( plan );

		driver.QueryResults["-- GetDeployedPlans"] = table;
	}

	/// <summary>Reads column 1 of a single-row <see cref="JournalingSlot.GetLatestRelease"/> result.</summary>
	private static bool ReadCompleted( object storedValue )
	{
		( FakeTransaction transaction, StatementJournal journal ) = Setup();

		DataTable table = new();
		table.Columns.Add( "release", typeof(string) );
		table.Columns.Add( "completed", typeof(object) );
		table.Rows.Add( "r1", storedValue );

		transaction.Driver.QueryResults["-- GetLatestRelease"] = table;

		return journal.GetLatestRelease( transaction )!.Completed;
	}

	private static Dictionary<JournalingSlot, string?> Complete()
		=> JournalingSlots.All.ToDictionary( slot => slot, slot => (string?)$"-- {slot}" );
}