using System.Data;

using Pug.Sqetch.Deployment;
using Pug.Sqetch.Planning.Models;

using static Pug.Sqetch.Tests.Deployment.Lineages;

namespace Pug.Sqetch.Tests.Deployment;

/// <summary>
/// The deployment lock: the optional AcquireLock and ReleaseLock journaling slots. Taken before the
/// journal is even provisioned, in a transaction of its own, and given back in another however the
/// deployment ends — without ever replacing the deployment's own outcome.
/// </summary>
public class DeploymentLockTests
{
	private const string Acquire = "journal-query #{0} journal AcquireLock [project=demo]";
	private const string Release = "journal-statement #{0} journal ReleaseLock [project=demo]";

	private readonly InMemoryScripts _scripts = new();
	private readonly FakeDatabaseDriver _driver = new();
	private readonly RecordingListener _listener = new();
	private readonly FakeJournal _journal = new();

	[Fact]
	public void TheLockIsTakenFirstInATransactionOfItsOwnCommittedAlone()
	{
		Deploy( OneRelease() );

		Assert.Equal(
			[ "begin #1", string.Format( Acquire, 1 ), "commit #1", "begin #2" ],
			_driver.Events.Take( 4 )
		);

		Assert.Equal( 1, _driver.Transactions[0].JournalingCalls.Count );
		Assert.True( _driver.Transactions[0].Committed );
		Assert.Equal( "journal PrepareJournal", _driver.Transactions[1].JournalingCalls[0].Statement );

		// the lock, like the journal's provisioning, is not a reported commit: only the deployment's one is
		Assert.Equal( "lock-acquired", _listener.Events[0] );
		Assert.Equal( 1, _listener.Events.Count( x => x == "lock-acquired" ) );
		Assert.Equal( 1, _listener.Events.Count( x => x == "committed" ) );
	}

	[Fact]
	public void ARefusedLockThrowsAndNothingElseRuns()
	{
		_journal.LockAvailable = false;

		Assert.Throws<DeploymentLockedException>( () => Deploy( OneRelease() ) );

		Assert.Equal( 1, _driver.Transactions.Count );
		Assert.True( _driver.Transactions[0].RolledBack );
		Assert.Equal( [ "begin #1", string.Format( Acquire, 1 ), "rollback #1" ], _driver.Events );
		Assert.Empty( _driver.AppliedScripts );
		Assert.Empty( _listener.Events );
	}

	[Fact]
	public void TheLockIsReleasedLastInATransactionOfItsOwnAfterASuccess()
	{
		Deploy( OneRelease() );

		AssertReleasedLast();
		Assert.Equal( [ "-- deploy a/s1" ], _driver.AppliedScripts );
	}

	[Fact]
	public void TheLockIsReleasedAfterAStepFailureAndTheFailurePropagates()
	{
		_driver.FailingScripts.Add( "-- deploy a/s1" );

		Assert.Throws<StepScriptFailedException>( () => Deploy( OneRelease() ) );

		AssertReleasedLast();
	}

	[Fact]
	public void TheLockIsReleasedWhenThereIsNothingToDeploy()
	{
		_journal.Latest = new JournaledRelease( "r1", Completed: true );

		Deploy( OneRelease() );

		Assert.Contains( "nothing", _listener.Events );
		AssertReleasedLast();
	}

	[Fact]
	public void TheLockIsReleasedAfterOnErrorCompensation()
	{
		_driver.FailingScripts.Add( "-- deploy b/s1" );

		Assert.Throws<StepScriptFailedException>( () => Deploy( TwoPlans(), DeploymentCommitLevel.Plan, RollbackTrigger.OnError ) );

		Assert.Contains( "-- rollback a/s1", _driver.AppliedScripts );
		Assert.Contains( "rolled-back", _listener.Events );
		AssertReleasedLast();
		AssertAfter( "journal-statement", "journal RolledBackPlan" );
	}

	[Fact]
	public void TheLockIsReleasedAfterOnSuccessCompensation()
	{
		Deploy( TwoPlans(), DeploymentCommitLevel.Release, RollbackTrigger.OnSuccess );

		Assert.Contains( "rolled-back", _listener.Events );
		AssertReleasedLast();
		AssertAfter( "journal-statement", "journal RolledBackRelease" );
	}

	[Fact]
	public void AFailingReleaseKeepsASuccessAndIsReported()
	{
		_driver.FailingJournalingStatements.Add( "journal ReleaseLock" );

		Deploy( OneRelease() );

		Assert.Equal( [ "-- deploy a/s1" ], _driver.AppliedScripts );
		Assert.Contains( _listener.Events, x => x.StartsWith( "lock-not-released" ) );
		Assert.True( _driver.Transactions[^1].RolledBack );
	}

	[Fact]
	public void AFailingReleaseKeepsTheOriginalFailure()
	{
		_driver.FailingScripts.Add( "-- deploy a/s1" );
		_driver.FailingJournalingStatements.Add( "journal ReleaseLock" );

		StepScriptFailedException error = Assert.Throws<StepScriptFailedException>( () => Deploy( OneRelease() ) );

		Assert.Contains( "a/s1", error.Message );
		Assert.Contains( _listener.Events, x => x.StartsWith( "lock-not-released" ) );
	}

	[Fact]
	public void AFailingReleaseWhoseRollbackFailsTooIsStillOnlyReported()
	{
		_driver.FailingJournalingStatements.Add( "journal ReleaseLock" );
		_driver.FailingRollback = true;

		Deploy( OneRelease() );

		Assert.Contains( _listener.Events, x => x.StartsWith( "lock-not-released" ) );
	}

	[Fact]
	public void AnUnsetLockRunsNothingAndOpensNoExtraTransaction()
	{
		Deploy( OneRelease(), withLock: false );

		Assert.DoesNotContain( _driver.Events, x => x.Contains( "Lock" ) );
		Assert.DoesNotContain( "lock-acquired", _listener.Events );

		// prepare, read and the release's one deployment transaction
		Assert.Equal( 3, _driver.Transactions.Count );
		Assert.Equal( "journal PrepareJournal", _driver.Transactions[0].JournalingCalls[0].Statement );
	}

	[Fact]
	public void TheLockAddsExactlyTwoTransactions()
	{
		Deploy( OneRelease() );

		Assert.Equal( 5, _driver.Transactions.Count );
	}

	[Fact]
	public void AFailingAcquireLockQueryIsAJournalingStatementExceptionAndNothingElseRuns()
	{
		_driver.FailingJournalingQueries.Add( "journal AcquireLock" );

		JournalingStatementException error = Assert.Throws<JournalingStatementException>( () => Deploy( OneRelease() ) );

		Assert.Contains( "AcquireLock", error.Message );
		Assert.Equal( 1, _driver.Transactions.Count );
		Assert.True( _driver.Transactions[0].RolledBack );
		Assert.DoesNotContain( _driver.Events, x => x.Contains( "ReleaseLock" ) );
		Assert.Empty( _listener.Events );
	}

	[Fact]
	public void AnAcquireLockQueryReturningNoRowRefusesTheDeployment()
	{
		_journal.Attach( _driver );

		_driver.QueryResults[FakeJournal.Marker( JournalingSlot.AcquireLock )] = new DataTable { Columns = { "acquired" } };

		Assert.Throws<DeploymentLockedException>( () => Engine( OneRelease() ) );

		Assert.Equal( 1, _driver.Transactions.Count );
		Assert.True( _driver.Transactions[0].RolledBack );
		Assert.DoesNotContain( _driver.Events, x => x.Contains( "ReleaseLock" ) );
	}

	[Fact]
	public void TheLockIsReleasedOnceAfterTheJournalCouldNotBePrepared()
	{
		_driver.FailingJournalingStatements.Add( "journal PrepareJournal" );

		Assert.ThrowsAny<Exception>( () => Deploy( OneRelease() ) );

		AssertReleasedOnce();
	}

	[Fact]
	public void TheLockIsReleasedOnceAfterTheBundleProvedIncompatible()
	{
		_journal.Latest = new JournaledRelease( "r1", Completed: false );
		_journal.DeployedPlans["r1"] = [ "a", "scrapped" ];

		Assert.Throws<IncompatibleBundleException>( () => Deploy( OneRelease() ) );

		AssertReleasedOnce();
	}

	[Fact]
	public void TheLockIsReleasedOnceAfterARollbackFailed()
	{
		_driver.FailingScripts.Add( "-- deploy b/s1" );
		_driver.FailingScripts.Add( "-- rollback a/s1" );

		Assert.Throws<RollbackFailedException>( () => Deploy( TwoPlans(), DeploymentCommitLevel.Plan, RollbackTrigger.OnError ) );

		AssertReleasedOnce();
	}

	[Fact]
	public void TheLockIsReleasedWhenTheCommitThatFollowedTheAcquiringQueryFails()
	{
		_driver.FailingCommits.Add( 1 );

		Assert.Throws<InvalidOperationException>( () => Deploy( OneRelease() ) );

		Assert.True( _driver.Transactions[0].RolledBack );
		Assert.Empty( _driver.AppliedScripts );
		AssertReleasedOnce();
	}

	[Fact]
	public void TheLockIsReleasedWhenTheListenerFailsToBeToldItWasAcquired()
	{
		_listener.FailingLockAcquired = true;

		Assert.Throws<InvalidOperationException>( () => Deploy( OneRelease() ) );

		Assert.Empty( _driver.AppliedScripts );
		AssertReleasedOnce();
	}

	private void AssertReleasedOnce()
	{
		Assert.Equal( 1, _driver.Events.Count( x => x.Contains( "journal ReleaseLock" ) ) );
		Assert.Equal( "journal ReleaseLock", _driver.Transactions[^1].JournalingCalls.Single().Statement );
		Assert.True( _driver.Transactions[^1].Committed );
	}

	private void AssertReleasedLast()
	{
		FakeTransaction last = _driver.Transactions[^1];

		Assert.Equal( 1, last.JournalingCalls.Count );
		Assert.Equal( "journal ReleaseLock", last.JournalingCalls[0].Statement );
		Assert.True( last.Committed );
		Assert.Equal( 1, _driver.Events.Count( x => x.Contains( "ReleaseLock" ) ) );
		Assert.Equal( string.Format( Release, last.Number ), _driver.Events[^2] );
		Assert.Equal( $"commit #{last.Number}", _driver.Events[^1] );
	}

	/// <summary>The release comes after the last statement matching <paramref name="marker"/>.</summary>
	private void AssertAfter( string kind, string marker )
	{
		int lastMarker = _driver.Events.FindLastIndex( x => x.StartsWith( kind ) && x.Contains( marker ) );
		int release = _driver.Events.FindIndex( x => x.Contains( "ReleaseLock" ) );

		Assert.True( lastMarker >= 0, $"no {marker} was journaled" );
		Assert.True( lastMarker < release );
	}

	private static IReadOnlyList<ReleaseGroup> OneRelease()
		=> Lineage( [ Release( "r1" ) ], [ Plan( "a", "r1", "s1" ) ] );

	private static IReadOnlyList<ReleaseGroup> TwoPlans()
		=> Lineage( [ Release( "r1" ) ], [ Plan( "a", "r1", "s1" ), Plan( "b", "r1", "s1" ) ] );

	private void Engine( IReadOnlyList<ReleaseGroup> releases )
		=> new DeploymentEngine( _driver, FakeJournal.Statements( true ), _scripts, DeploymentCommitLevel.Release, _listener )
			.Deploy( Project, releases );

	private void Deploy(
		IReadOnlyList<ReleaseGroup> releases,
		DeploymentCommitLevel commitLevel = DeploymentCommitLevel.Release,
		RollbackTrigger rollback = RollbackTrigger.None,
		bool withLock = true
	)
	{
		_journal.Attach( _driver );

		new DeploymentEngine( _driver, FakeJournal.Statements( withLock ), _scripts, commitLevel, _listener, rollback )
			.Deploy( Project, releases );
	}
}
