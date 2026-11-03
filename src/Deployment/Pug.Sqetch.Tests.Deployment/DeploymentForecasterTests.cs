using Pug.Sqetch.Deployment;
using Pug.Sqetch.Planning.Models;

using static Pug.Sqetch.Tests.Deployment.Lineages;

namespace Pug.Sqetch.Tests.Deployment;

/// <summary>
/// <see cref="DeploymentForecaster"/> reads the journal and decides through
/// <see cref="DeploymentPlanner"/>; what these pin is that it only ever reads — its one transaction
/// is rolled back, never committed, whatever happens — and which queries it asks, since each is a
/// round trip to a database the caller is looking at precisely because it must not be touched.
/// </summary>
public class DeploymentForecasterTests
{
	private readonly FakeDatabaseDriver _driver = new();
	private readonly FakeJournal _journal = new();

	private static IReadOnlyList<ReleaseGroup> Releases()
		=> Lineage(
			[ Release( "r1" ), Release( "r2", "r1" ) ],
			[ Plan( "a", "r1", "s" ), Plan( "b", "r1", "s" ), Plan( "c", "r2", "s" ) ] );

	private DeploymentStatus Forecast( IReadOnlyList<ReleaseGroup> groups, JournalingStatements? statements = null )
	{
		_journal.Attach( _driver );

		return new DeploymentForecaster( _driver, statements ?? FakeJournal.Statements() )
			.Forecast( Lineages.Project, groups );
	}

	private List<string> Queries()
		=> _driver.Transactions.SelectMany( transaction => transaction.JournalingCalls ).Select( call => call.Statement ).ToList();

	private void AssertOnlyReadAndRolledBack()
	{
		FakeTransaction transaction = Assert.Single( _driver.Transactions );

		Assert.True( transaction.RolledBack );
		Assert.False( transaction.Committed );
		Assert.Empty( _driver.AppliedScripts );
		Assert.DoesNotContain( _driver.Events, e => e.StartsWith( "commit" ) );
		Assert.DoesNotContain( _driver.Events, e => e.StartsWith( "journal-statement" ) );
		Assert.DoesNotContain( "journal PrepareJournal", Queries() );
	}

	[Fact]
	public void AnUnsetJournalExistsIsRefusedNamingItBeforeAnyTransactionIsBegun()
	{
		JournalingStatementsNotSetException error = Assert.Throws<JournalingStatementsNotSetException>( () =>
			Forecast( Releases(), FakeJournal.RequiredStatements() ) );

		Assert.Equal( [ "JournalExists" ], error.Slots );
		Assert.Equal( DeploymentForecaster.JournalExistsPurpose, error.Purpose );
		Assert.Contains( "JournalExists", error.Message );
		Assert.Empty( _driver.Transactions );
		Assert.Empty( _driver.Events );
	}

	[Fact]
	public void AJournalThatDoesNotExistIsAFreshDatabaseAndNoOtherQueryRuns()
	{
		_journal.JournalExists = false;

		DeploymentStatus status = Forecast( Releases() );

		Assert.False( status.JournalExists );
		Assert.Null( status.Forecast.Journaled );
		Assert.Equal( [ "journal JournalExists" ], Queries() );
		Assert.Equal( 3, status.Forecast.Releases.Sum( release => release.PlansToDeploy.Count() ) );
		AssertOnlyReadAndRolledBack();
	}

	[Fact]
	public void AFreshDatabaseStillRefusesAContinuationBundle()
	{
		_journal.JournalExists = false;

		Assert.Throws<IncompatibleBundleException>( () => Forecast( Releases().Skip( 1 ).ToList() ) );

		Assert.True( Assert.Single( _driver.Transactions ).RolledBack );
		Assert.Equal( [ "journal JournalExists" ], Queries() );
	}

	[Fact]
	public void AJournalHoldingNothingIsEmptyAndDeploysEverything()
	{
		DeploymentStatus status = Forecast( Releases() );

		Assert.True( status.JournalExists );
		Assert.Null( status.Forecast.Journaled );
		Assert.Equal( [ "journal JournalExists", "journal GetLatestRelease" ], Queries() );
		Assert.False( status.Forecast.NothingToDeploy );
		AssertOnlyReadAndRolledBack();
	}

	[Fact]
	public void AnIncompleteReleaseIsResumedAndItsDeployedPlansAreQueried()
	{
		_journal.Latest = new JournaledRelease( "r1", Completed: false );
		_journal.DeployedPlans["r1"] = [ "a" ];

		DeploymentStatus status = Forecast( Releases() );

		Assert.Equal(
			[ "journal JournalExists", "journal GetLatestRelease", "journal GetDeployedPlans" ],
			Queries() );

		Assert.Equal( [ "a" ], status.Forecast.Releases[0].SkippedPlans );
		Assert.Equal( [ "b" ], status.Forecast.Releases[0].PlansToDeploy.Select( plan => plan.Name ) );
		AssertOnlyReadAndRolledBack();
	}

	[Fact]
	public void ACompletedReleaseHandsOverWithoutQueryingItsPlans()
	{
		_journal.Latest = new JournaledRelease( "r1", Completed: true );

		DeploymentStatus status = Forecast( Releases() );

		Assert.DoesNotContain( "journal GetDeployedPlans", Queries() );
		Assert.Equal( [ "c" ], status.Forecast.Releases.SelectMany( release => release.PlansToDeploy ).Select( plan => plan.Name ) );
		AssertOnlyReadAndRolledBack();
	}

	[Fact]
	public void AContinuationBundleReportsWhereItContinuesFrom()
	{
		_journal.Latest = new JournaledRelease( "r1", Completed: true );

		DeploymentStatus status = Forecast( Releases().Skip( 1 ).ToList() );

		Assert.Equal( "r1", status.Forecast.ContinuingFrom );
		AssertOnlyReadAndRolledBack();
	}

	[Fact]
	public void ADatabaseAtTheBundlesLastReleaseHasNothingToDeploy()
	{
		_journal.Latest = new JournaledRelease( "r2", Completed: true );

		DeploymentStatus status = Forecast( Releases() );

		Assert.True( status.Forecast.NothingToDeploy );
		AssertOnlyReadAndRolledBack();
	}

	[Fact]
	public void APartlyDeployedReleaseTheBundleLacksIsRefusedBeforeItsPlansAreQueried()
	{
		_journal.Latest = new JournaledRelease( "r1", Completed: false );

		Assert.Throws<IncompatibleBundleException>( () => Forecast( Releases().Skip( 1 ).ToList() ) );

		Assert.DoesNotContain( "journal GetDeployedPlans", Queries() );
		Assert.True( Assert.Single( _driver.Transactions ).RolledBack );
		Assert.False( _driver.Transactions[0].Committed );
	}

	[Fact]
	public void AJournaledPlanTheBundleLacksIsRefusedAndRolledBack()
	{
		_journal.Latest = new JournaledRelease( "r1", Completed: false );
		_journal.DeployedPlans["r1"] = [ "a", "gone" ];

		IncompatibleBundleException error = Assert.Throws<IncompatibleBundleException>( () => Forecast( Releases() ) );

		Assert.Contains( "'gone'", error.Message );
		Assert.True( Assert.Single( _driver.Transactions ).RolledBack );
		Assert.False( _driver.Transactions[0].Committed );
	}

	[Fact]
	public void ADatabaseAtAnUnrelatedReleaseIsRefusedAndRolledBack()
	{
		_journal.Latest = new JournaledRelease( "elsewhere", Completed: true );

		Assert.Throws<IncompatibleBundleException>( () => Forecast( Releases() ) );

		Assert.True( Assert.Single( _driver.Transactions ).RolledBack );
		Assert.False( _driver.Transactions[0].Committed );
	}

	[Fact]
	public void AFailingJournalQueryIsRolledBackAndNeverCommitted()
	{
		_journal.Attach( _driver );
		_driver.FailingJournalingQueries.Add( "journal GetLatestRelease" );

		Assert.Throws<JournalingStatementException>( () =>
			new DeploymentForecaster( _driver, FakeJournal.Statements() ).Forecast( Lineages.Project, Releases() ) );

		Assert.True( Assert.Single( _driver.Transactions ).RolledBack );
		Assert.False( _driver.Transactions[0].Committed );
	}

	[Fact]
	public void ARollbackFailingAfterAFailedQueryDoesNotHideTheQueryFailure()
	{
		_journal.Attach( _driver );
		_driver.FailingJournalingQueries.Add( "journal GetLatestRelease" );
		_driver.FailingRollback = true;

		Assert.Throws<JournalingStatementException>( () =>
			new DeploymentForecaster( _driver, FakeJournal.Statements() ).Forecast( Lineages.Project, Releases() ) );

		Assert.True( Assert.Single( _driver.Transactions ).RolledBack );
	}

	[Fact]
	public void AFailingJournalExistsQueryIsRolledBackAndNeverCommitted()
	{
		_journal.Attach( _driver );
		_driver.FailingJournalingQueries.Add( "journal JournalExists" );

		Assert.Throws<JournalingStatementException>( () =>
			new DeploymentForecaster( _driver, FakeJournal.Statements() ).Forecast( Lineages.Project, Releases() ) );

		Assert.True( Assert.Single( _driver.Transactions ).RolledBack );
		Assert.False( _driver.Transactions[0].Committed );
	}

	[Fact]
	public void ABrokenLineageIsRefusedBeforeTheDatabaseIsTouched()
	{
		IReadOnlyList<ReleaseGroup> groups = Releases();

		Assert.Throws<ArgumentException>( () => Forecast( [ groups[1], groups[0] ] ) );

		Assert.Empty( _driver.Transactions );
	}

	[Fact]
	public void TheProjectNameIsWhatTheQueriesAreGiven()
	{
		Forecast( Releases() );

		Assert.All(
			_driver.Transactions.SelectMany( transaction => transaction.JournalingCalls ),
			call => Assert.Contains( new Pug.Sqetch.DatabaseDriver.JournalingParameter( "project", Lineages.Project ), call.Parameters ) );
	}
}
