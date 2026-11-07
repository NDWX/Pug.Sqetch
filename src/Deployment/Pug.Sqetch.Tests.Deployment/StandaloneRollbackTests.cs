using Pug.Sqetch.Deployment;
using Pug.Sqetch.Planning.Models;

using static Pug.Sqetch.Tests.Deployment.Lineages;

namespace Pug.Sqetch.Tests.Deployment;

/// <summary>
/// <see cref="DeploymentEngine.RollBack"/>: undoing what the journal says is deployed, through the fakes.
/// <see cref="RollbackPlannerTests"/> pins which releases and plans; these pin what the engine does with
/// them — the scripts, the journal entries, the transactions and the failures.
/// </summary>
public class StandaloneRollbackTests
{
	private readonly InMemoryScripts _scripts = new();
	private readonly FakeDatabaseDriver _driver = new();
	private readonly RecordingListener _listener = new();
	private readonly FakeJournal _journal = new();

	/// <summary>r1 holds a (s1, s2) then b (s1); r2 holds c (s1).</summary>
	private static IReadOnlyList<ReleaseGroup> TwoReleases()
		=> Lineage(
			[ Release( "r1" ), Release( "r2", "r1" ) ],
			[ Plan( "a", "r1", "s1", "s2" ), Plan( "b", "r1", "s1" ), Plan( "c", "r2", "s1" ) ]
		);

	/// <summary>Seeds the journal as a complete deployment of <see cref="TwoReleases"/>.</summary>
	private void SeedDeployed()
	{
		_journal.Latest = new JournaledRelease( "r2", Completed: true );
		_journal.DeployedPlans["r1"] = [ "a", "b" ];
		_journal.DeployedPlans["r2"] = [ "c" ];
	}

	private List<string> RollbackScripts()
		=> _driver.AppliedScripts.Where( script => script.StartsWith( "-- rollback" ) ).ToList();

	private List<string> RollbackJournal()
		=> _driver.Transactions
			.SelectMany( transaction => transaction.Journaled )
			.Where( entry => entry.Contains( "olledBack" ) || entry.Contains( "ollingBack" ) )
			.ToList();

	// ------------------------------------------------------------------ the scripts

	[Fact]
	public void ScriptsRunInExactReverseOrderReleasesThenPlansThenSteps()
	{
		SeedDeployed();

		RollBack( TwoReleases(), RollbackTarget.All );

		Assert.Equal(
			[ "-- rollback c/s1", "-- rollback b/s1", "-- rollback a/s2", "-- rollback a/s1" ],
			RollbackScripts()
		);
		Assert.Equal( 4, _driver.AppliedScripts.Count );
	}

	[Fact]
	public void ToReleaseLeavesTheNamedReleaseAlone()
	{
		SeedDeployed();

		RollBack( TwoReleases(), RollbackTarget.ToRelease( "r1" ) );

		Assert.Equal( [ "-- rollback c/s1" ], _driver.AppliedScripts );
		Assert.Equal( [ "r2" ], _scripts.Releases.Distinct() );
	}

	[Fact]
	public void OnlyJournaledPlansAreUndone()
	{
		_journal.Latest = new JournaledRelease( "r2", Completed: false );
		_journal.DeployedPlans["r1"] = [ "b" ];
		_journal.DeployedPlans["r2"] = [ ];

		RollBack( TwoReleases(), RollbackTarget.All );

		Assert.Equal( [ "-- rollback b/s1" ], _driver.AppliedScripts );

		// the incomplete release with no plans still has its start undone
		Assert.Contains( "RollingBackRelease r2", RollbackJournal() );
		Assert.Contains( "RolledBackRelease r2", RollbackJournal() );
	}

	[Fact]
	public void OfPlansUndoesJustThosePlans()
	{
		SeedDeployed();

		RollBack( TwoReleases(), RollbackTarget.OfPlans( [ "c" ] ) );

		Assert.Equal( [ "-- rollback c/s1" ], _driver.AppliedScripts );
		Assert.Equal( [ "c" ], _listener.Events.Where( x => x.StartsWith( "rollback-plan" ) ).Select( x => x[(x.IndexOf( '/' ) + 1)..] ) );
	}

	// ------------------------------------------------------------------ the journal

	[Fact]
	public void RollbackIsJournaledThroughItsOwnSlotsInReverse()
	{
		SeedDeployed();

		RollBack( TwoReleases(), RollbackTarget.All );

		Assert.Equal(
			[
				"RollingBackRelease r2",
				"RollingBackPlan r2/c",
				"RollingBackStep r2/c/s1",
				"RolledBackStep r2/c/s1",
				"RolledBackPlan r2/c",
				"RolledBackRelease r2",
				"RollingBackRelease r1",
				"RollingBackPlan r1/b",
				"RollingBackStep r1/b/s1",
				"RolledBackStep r1/b/s1",
				"RolledBackPlan r1/b",
				"RollingBackPlan r1/a",
				"RollingBackStep r1/a/s2",
				"RolledBackStep r1/a/s2",
				"RollingBackStep r1/a/s1",
				"RolledBackStep r1/a/s1",
				"RolledBackPlan r1/a",
				"RolledBackRelease r1"
			],
			RollbackJournal()
		);
	}

	[Fact]
	public void TheJournalHoldsNothingOfTheUndonePlansAfterwards()
	{
		SeedDeployed();

		RollBack( TwoReleases(), RollbackTarget.ToRelease( "r1" ) );

		Assert.Empty( _journal.DeployedPlans["r2"] );
		Assert.Equal( [ "a", "b" ], _journal.DeployedPlans["r1"] );
	}

	[Fact]
	public void ListenerIsToldOfEachReleaseAndPlanThenTheEnd()
	{
		SeedDeployed();

		RollBack( TwoReleases(), RollbackTarget.All );

		Assert.Equal(
			[
				"rolling-back everything deployed",
				"rollback-release r2",
				"rollback-plan r2/c",
				"rollback-release r1",
				"rollback-plan r1/b",
				"rollback-plan r1/a",
				"rolled-back"
			],
			_listener.Events
		);
	}

	[Fact]
	public void DeployRollbackNowAnnouncesEachReleaseToo()
	{
		_journal.Attach( _driver );

		new DeploymentEngine(
				_driver,
				FakeJournal.Statements(),
				_scripts,
				DeploymentCommitLevel.Release,
				_listener,
				RollbackTrigger.OnSuccess
			)
			.Deploy( Project, TwoReleases() );

		Assert.Equal(
			[ "rollback-release r2", "rollback-release r1" ],
			_listener.Events.Where( x => x.StartsWith( "rollback-release" ) )
		);
	}

	// ------------------------------------------------------------------ transactions

	[Fact]
	public void TheJournalIsReadInOneTransactionThenUndoneByReleaseWithoutBeingPrepared()
	{
		SeedDeployed();

		RollBack( TwoReleases(), RollbackTarget.ToRelease( "r1" ) );

		Assert.Equal( 2, _driver.Transactions.Count );
		Assert.All( _driver.Transactions, transaction => Assert.True( transaction.Committed ) );

		Assert.Equal(
			[ "journal GetLatestRelease", "journal GetDeployedPlans" ],
			_driver.Transactions[0].JournalingCalls.Select( x => x.Statement )
		);
		Assert.DoesNotContain(
			_driver.Transactions.SelectMany( transaction => transaction.JournalingCalls ),
			call => call.Statement == "journal PrepareJournal"
		);

		// only r2 is undone, so only r2's plans are read
		Assert.Equal(
			"r2",
			_driver.Transactions[0].JournalingCalls[1].Parameters.Single( p => p.Name == "release" ).Value
		);
	}

	[Fact]
	public void TheReadAsksForEveryReleaseItWillUndo()
	{
		SeedDeployed();

		RollBack( TwoReleases(), RollbackTarget.All );

		Assert.Equal(
			[ "r2", "r1" ],
			_driver.Transactions[0].JournalingCalls
				.Where( x => x.Statement == "journal GetDeployedPlans" )
				.Select( x => x.Parameters.Single( p => p.Name == "release" ).Value )
		);
	}

	[Fact]
	public void ReleaseLevelCommitsOncePerRelease()
	{
		SeedDeployed();

		RollBack( TwoReleases(), RollbackTarget.All, DeploymentCommitLevel.Release );

		// read, r2, r1
		Assert.Equal( 3, _driver.Transactions.Count );
		Assert.All( _driver.Transactions, transaction => Assert.True( transaction.Committed ) );

		Assert.Equal( "RolledBackRelease r2", _driver.Transactions[1].Journaled[^1] );
		Assert.Equal( "RolledBackRelease r1", _driver.Transactions[2].Journaled[^1] );
		Assert.Contains( "RolledBackPlan r1/b", _driver.Transactions[2].Journaled );
		Assert.Contains( "RolledBackPlan r1/a", _driver.Transactions[2].Journaled );
	}

	[Fact]
	public void PlanLevelCommitsOncePerPlanAndTheReleaseEndsWithItsLastPlan()
	{
		SeedDeployed();

		RollBack( TwoReleases(), RollbackTarget.All, DeploymentCommitLevel.Plan );

		// read, r2 (its only plan), r1/b, r1/a with the release
		Assert.Equal( 4, _driver.Transactions.Count );
		Assert.All( _driver.Transactions, transaction => Assert.True( transaction.Committed ) );

		Assert.Equal( "RolledBackPlan r1/b", _driver.Transactions[2].Journaled[^1] );
		Assert.Equal(
			[ "RolledBackPlan r1/a", "RolledBackRelease r1" ],
			_driver.Transactions[3].Journaled.TakeLast( 2 )
		);
	}

	// ------------------------------------------------------------------ the lock

	[Fact]
	public void TheLockIsTakenFirstAndReleasedLast()
	{
		SeedDeployed();

		RollBack( TwoReleases(), RollbackTarget.All, withLock: true );

		Assert.Equal( "journal AcquireLock", _driver.Transactions[0].JournalingCalls[0].Statement );
		Assert.Equal( "journal GetLatestRelease", _driver.Transactions[1].JournalingCalls[0].Statement );
		Assert.DoesNotContain(
			_driver.Transactions.SelectMany( transaction => transaction.JournalingCalls ),
			call => call.Statement == "journal PrepareJournal"
		);
		Assert.Equal( "journal ReleaseLock", _driver.Transactions[^1].JournalingCalls[0].Statement );
		Assert.Equal( "lock-acquired", _listener.Events[0] );
		Assert.Equal( 4, RollbackScripts().Count );
	}

	[Fact]
	public void ARefusedLockRunsNothingElse()
	{
		SeedDeployed();
		_journal.LockAvailable = false;

		Assert.Throws<DeploymentLockedException>( () => RollBack( TwoReleases(), RollbackTarget.All, withLock: true ) );

		Assert.Single( _driver.Transactions );
		Assert.Empty( _driver.AppliedScripts );
		Assert.Empty( _listener.Events );
	}

	[Fact]
	public void TheLockIsReleasedAfterAFailingRollbackScript()
	{
		SeedDeployed();
		_driver.FailingScripts.Add( "-- rollback c/s1" );

		Assert.Throws<RollbackFailedException>( () => RollBack( TwoReleases(), RollbackTarget.All, withLock: true ) );

		Assert.Equal( "journal ReleaseLock", _driver.Transactions[^1].JournalingCalls[0].Statement );
		Assert.True( _driver.Transactions[^1].Committed );
	}

	[Fact]
	public void TheLockIsReleasedWhenThereIsNothingToRollBack()
	{
		RollBack( TwoReleases(), RollbackTarget.All, withLock: true );

		Assert.Contains( "nothing-to-roll-back", _listener.Events );
		Assert.Equal( "journal ReleaseLock", _driver.Transactions[^1].JournalingCalls[0].Statement );
	}

	// ------------------------------------------------------------------ refusals

	[Fact]
	public void AReleaseOutsideTheLineageIsRefusedWithoutAnyTransaction()
	{
		SeedDeployed();

		Assert.Throws<RollbackRefusedException>( () => RollBack( TwoReleases(), RollbackTarget.ToRelease( "nope" ), withLock: true ) );

		Assert.Empty( _driver.Transactions );
		Assert.Empty( _driver.Events );
		Assert.Empty( _listener.Events );
	}

	[Fact]
	public void ABrokenLineageIsRefusedWithoutAnyTransaction()
	{
		IReadOnlyList<ReleaseGroup> broken = Lineage( [ Release( "r1" ), Release( "r2", "elsewhere" ) ], [ ] );

		Assert.Throws<ArgumentException>( () => RollBack( broken, RollbackTarget.All ) );

		Assert.Empty( _driver.Transactions );
	}

	[Fact]
	public void APlanOfAnEarlierReleaseIsRefusedAfterTheReadAndUndoesNothing()
	{
		SeedDeployed();

		Assert.Throws<RollbackRefusedException>( () => RollBack( TwoReleases(), RollbackTarget.OfPlans( [ "a" ] ) ) );

		Assert.Empty( _driver.AppliedScripts );
		Assert.Empty( RollbackJournal() );
		Assert.DoesNotContain( _listener.Events, x => x.StartsWith( "rolling-back" ) );
		Assert.All( _driver.Transactions, transaction => Assert.True( transaction.Committed || transaction.RolledBack ) );
	}

	[Fact]
	public void AnUnknownPlanIsRefusedWithoutAnyTransaction()
	{
		SeedDeployed();

		Assert.Throws<RollbackRefusedException>( () => RollBack( TwoReleases(), RollbackTarget.OfPlans( [ "nope" ] ), withLock: true ) );

		Assert.Empty( _driver.Transactions );
		Assert.Empty( _driver.Events );
		Assert.Empty( _listener.Events );
	}

	[Fact]
	public void ALatestReleaseTheGroupsLackIsIncompatibleAndUndoesNothing()
	{
		_journal.Latest = new JournaledRelease( "elsewhere", Completed: true );

		Assert.Throws<IncompatibleBundleException>( () => RollBack( TwoReleases(), RollbackTarget.All ) );

		Assert.Empty( _driver.AppliedScripts );
		Assert.Empty( RollbackJournal() );
	}

	[Fact]
	public void AllIsRefusedForAContinuationBundle()
	{
		SeedDeployed();

		Assert.Throws<IncompatibleBundleException>( () => RollBack( TwoReleases().Skip( 1 ).ToList(), RollbackTarget.All ) );

		Assert.Empty( _driver.AppliedScripts );
	}

	[Fact]
	public void AJournaledPlanTheGroupsLackIsIncompatible()
	{
		SeedDeployed();
		_journal.DeployedPlans["r2"] = [ "c", "ghost" ];

		Assert.Throws<IncompatibleBundleException>( () => RollBack( TwoReleases(), RollbackTarget.All ) );

		Assert.Empty( _driver.AppliedScripts );
	}

	// ------------------------------------------------------------------ nothing to roll back

	[Fact]
	public void NothingJournaledIsReportedAndRunsNoScript()
	{
		RollBack( TwoReleases(), RollbackTarget.All );

		Assert.Equal( [ "nothing-to-roll-back" ], _listener.Events );
		Assert.Empty( _driver.AppliedScripts );
		Assert.Empty( RollbackJournal() );
		Assert.Single( _driver.Transactions );
		Assert.All( _driver.Transactions, transaction => Assert.True( transaction.Committed ) );
	}

	[Fact]
	public void ToTheLatestReleaseIsReported()
	{
		SeedDeployed();

		RollBack( TwoReleases(), RollbackTarget.ToRelease( "r2" ) );

		Assert.Equal( [ "nothing-to-roll-back" ], _listener.Events );
		Assert.Empty( _driver.AppliedScripts );
	}

	[Fact]
	public void PlansAlreadyUndoneAreReported()
	{
		_journal.Latest = new JournaledRelease( "r2", Completed: false );
		_journal.DeployedPlans["r1"] = [ "a", "b" ];

		RollBack( TwoReleases(), RollbackTarget.OfPlans( [ "c" ] ) );

		Assert.Equal( [ "nothing-to-roll-back" ], _listener.Events );
		Assert.Empty( _driver.AppliedScripts );
	}

	// ------------------------------------------------------------------ failure

	[Fact]
	public void AFailingRollbackScriptNamesTheStepAndEarlierReleaseCommitsStand()
	{
		SeedDeployed();
		_driver.FailingScripts.Add( "-- rollback b/s1" );

		RollbackFailedException error = Assert.Throws<RollbackFailedException>( () => RollBack(
				TwoReleases(),
				RollbackTarget.All,
				DeploymentCommitLevel.Release
			)
		);

		Assert.Equal( ( "r1", "b", "s1" ), ( error.Release, error.Plan, error.Step ) );

		// r2 committed before r1 began; r1's transaction took its own entries back
		Assert.Equal( [ "-- rollback c/s1" ], _driver.AppliedScripts );
		Assert.Empty( _journal.DeployedPlans["r2"] );
		Assert.Equal( [ "a", "b" ], _journal.DeployedPlans["r1"] );
		Assert.True( _driver.Transactions[^1].RolledBack );
		Assert.DoesNotContain( "rolled-back", _listener.Events );
	}

	[Fact]
	public void RunningTheSameRollbackAgainFinishesWhatAFailureLeftAtReleaseLevel()
	{
		SeedDeployed();
		_driver.FailingScripts.Add( "-- rollback b/s1" );

		Assert.Throws<RollbackFailedException>( () => RollBack( TwoReleases(), RollbackTarget.All ) );

		// what the journal says now: r2 is undone, so the database is at r1, deployed whole
		_driver.FailingScripts.Clear();
		_journal.Latest = new JournaledRelease( "r1", Completed: true );
		_listener.Events.Clear();

		RollBack( TwoReleases(), RollbackTarget.All );

		Assert.Equal(
			[ "-- rollback c/s1", "-- rollback b/s1", "-- rollback a/s2", "-- rollback a/s1" ],
			_driver.AppliedScripts
		);
		Assert.Empty( _journal.DeployedPlans["r1"] );
		Assert.Equal( "rolled-back", _listener.Events[^1] );
	}

	[Fact]
	public void RunningTheSameRollbackAgainFinishesWhatAFailureLeftAtPlanLevel()
	{
		SeedDeployed();
		_driver.FailingScripts.Add( "-- rollback a/s2" );

		Assert.Throws<RollbackFailedException>( () => RollBack(
				TwoReleases(),
				RollbackTarget.All,
				DeploymentCommitLevel.Plan
			)
		);

		// r2 and plan b committed; a's transaction did not
		Assert.Equal( [ "-- rollback c/s1", "-- rollback b/s1" ], _driver.AppliedScripts );
		Assert.Empty( _journal.DeployedPlans["r2"] );
		Assert.Equal( [ "a" ], _journal.DeployedPlans["r1"] );

		// the journal now reads r1 incomplete, by the templates' rule: its RollingBackRelease committed
		Assert.Equal( new JournaledRelease( "r1", Completed: false ), _journal.Latest );

		_driver.FailingScripts.Clear();
		_listener.Events.Clear();

		RollBack( TwoReleases(), RollbackTarget.All, DeploymentCommitLevel.Plan );

		Assert.Equal(
			[ "-- rollback c/s1", "-- rollback b/s1", "-- rollback a/s2", "-- rollback a/s1" ],
			_driver.AppliedScripts
		);
		Assert.Empty( _journal.DeployedPlans["r1"] );
		Assert.Equal( [ "rolling-back everything deployed", "rollback-release r1", "rollback-plan r1/a", "rolled-back" ], _listener.Events );
	}

	[Fact]
	public void AFailureReadingTheJournalRollsBackAndUndoesNothing()
	{
		SeedDeployed();
		_driver.FailingJournalingQueries.Add( "journal GetDeployedPlans" );

		Assert.ThrowsAny<Exception>( () => RollBack( TwoReleases(), RollbackTarget.All ) );

		Assert.Empty( _driver.AppliedScripts );
		Assert.True( _driver.Transactions[0].RolledBack );
	}

	[Fact]
	public void AFailingRollbackScriptIsNotReplacedByTheTransactionsFailingRollback()
	{
		SeedDeployed();
		_driver.FailingScripts.Add( "-- rollback c/s1" );
		_driver.FailingRollback = true;

		RollbackFailedException error = Assert.Throws<RollbackFailedException>( () => RollBack( TwoReleases(), RollbackTarget.All ) );

		Assert.Equal( ( "r2", "c", "s1" ), ( error.Release, error.Plan, error.Step ) );
		Assert.True( _driver.Transactions[^1].RolledBack );
	}

	[Fact]
	public void AFailingJournalQueryIsNotReplacedByTheTransactionsFailingRollback()
	{
		SeedDeployed();
		_driver.FailingJournalingQueries.Add( "journal GetDeployedPlans" );
		_driver.FailingRollback = true;

		JournalingStatementException error = Assert.Throws<JournalingStatementException>( () => RollBack( TwoReleases(), RollbackTarget.All ) );

		Assert.Contains( "journaling query rejected", error.InnerException!.Message );
		Assert.Empty( _driver.AppliedScripts );
		Assert.True( _driver.Transactions[0].RolledBack );
	}

	[Fact]
	public void ADatabaseWithNoJournalFailsTheReadAndRollsBackWithNothingElseRun()
	{
		_driver.FailingJournalingQueries.Add( FakeJournal.Marker( JournalingSlot.GetLatestRelease ) );

		JournalingStatementException error = Assert.Throws<JournalingStatementException>( () => RollBack( TwoReleases(), RollbackTarget.All ) );

		Assert.Equal( nameof(JournalingSlot.GetLatestRelease), error.Slot );
		Assert.Single( _driver.Transactions );
		Assert.True( _driver.Transactions[0].RolledBack );
		Assert.False( _driver.Transactions[0].Committed );
		Assert.Empty( _driver.AppliedScripts );
		Assert.Empty( RollbackJournal() );
		Assert.Empty( _listener.Events );
	}

	[Fact]
	public void TheLockIsReleasedWhenTheJournalCannotBeRead()
	{
		_driver.FailingJournalingQueries.Add( FakeJournal.Marker( JournalingSlot.GetLatestRelease ) );

		Assert.Throws<JournalingStatementException>( () => RollBack( TwoReleases(), RollbackTarget.All, withLock: true ) );

		// lock, the failed read, release
		Assert.Equal( 3, _driver.Transactions.Count );
		Assert.Equal( "journal AcquireLock", _driver.Transactions[0].JournalingCalls[0].Statement );
		Assert.True( _driver.Transactions[1].RolledBack );
		Assert.Equal( "journal ReleaseLock", _driver.Transactions[2].JournalingCalls[0].Statement );
		Assert.True( _driver.Transactions[2].Committed );
		Assert.Empty( _driver.AppliedScripts );
	}

	// ------------------------------------------------------------------ a deployment, then its rollback

	[Fact]
	public void ADeploymentTheEngineMadeCanBeUndoneToItsFirstRelease()
	{
		DeploymentEngine engine = Engine( DeploymentCommitLevel.Release );

		_journal.Attach( _driver );

		engine.Deploy( Project, TwoReleases() );
		engine.RollBack( Project, TwoReleases(), RollbackTarget.ToRelease( "r1" ) );

		Assert.Equal(
			[ "-- deploy a/s1", "-- deploy a/s2", "-- deploy b/s1", "-- deploy c/s1", "-- rollback c/s1" ],
			_driver.AppliedScripts
		);
		Assert.Empty( _journal.DeployedPlans["r2"] );
	}

	// ------------------------------------------------------------------ helpers

	private DeploymentEngine Engine( DeploymentCommitLevel commitLevel, bool withLock = false )
		=> new( _driver, FakeJournal.Statements( withLock ), _scripts, commitLevel, _listener );

	private void RollBack(
		IReadOnlyList<ReleaseGroup> groups,
		RollbackTarget target,
		DeploymentCommitLevel commitLevel = DeploymentCommitLevel.Release,
		bool withLock = false
	)
	{
		_journal.Attach( _driver );

		Engine( commitLevel, withLock ).RollBack( Project, groups, target );
	}
}
