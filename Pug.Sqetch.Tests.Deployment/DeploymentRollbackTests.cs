using Pug.Sqetch.Bundling;
using Pug.Sqetch.Bundling.Layouts;
using Pug.Sqetch.Deployment;
using static Pug.Sqetch.Tests.Deployment.Manifests;

namespace Pug.Sqetch.Tests.Deployment;

/// <summary>
/// Rollback is a compensating pass, not a transaction rollback: it runs the rollback script of
/// everything the run <em>committed</em>, in reverse. That is the whole point — at plan commit
/// level the earlier plans are already durable, so no transaction could take them back — and it is
/// why these tests assert the applied scripts rather than transaction state.
/// </summary>
public class DeploymentRollbackTests
{
	private readonly DefaultBundleLayout _layout = new ();
	private readonly FakeDatabaseDriver _driver = new ();
	private readonly RecordingListener _listener = new ();
	private readonly FakeJournal _journal = new ();

	[Fact]
	public void WithoutTheModeAFailureLeavesEarlierCommitsInPlace()
	{
		_driver.FailingScripts.Add( "-- deploy c/s1" );

		Assert.Throws<StepScriptFailedException>(
			() => Deploy( TwoReleases(), DeploymentCommitLevel.Release, RollbackTrigger.None ) );

		// a/s1 and b/s1 committed with r1 and stay; nothing is undone
		Assert.Equal( ["-- deploy a/s1", "-- deploy b/s1"], _driver.AppliedScripts );
	}

	/// <summary>
	/// The failing release's own transaction took its plans back, so only the committed release is
	/// compensated — and in reverse, innermost first.
	/// </summary>
	[Fact]
	public void OnErrorUndoesWhatCommittedInReverse()
	{
		_driver.FailingScripts.Add( "-- deploy c/s1" );

		Assert.Throws<StepScriptFailedException>(
			() => Deploy( TwoReleases(), DeploymentCommitLevel.Release, RollbackTrigger.OnError ) );

		Assert.Equal(
			["-- deploy a/s1", "-- deploy b/s1", "-- rollback b/s1", "-- rollback a/s1"],
			_driver.AppliedScripts );
	}

	/// <summary>
	/// The case a transaction cannot cover: at plan commit level every plan before the failure is
	/// already durable, so only a compensating pass can undo them.
	/// </summary>
	[Fact]
	public void OnErrorUndoesPlansAlreadyCommittedAtPlanCommitLevel()
	{
		_driver.FailingScripts.Add( "-- deploy c/s1" );

		Assert.Throws<StepScriptFailedException>(
			() => Deploy( TwoReleases(), DeploymentCommitLevel.Plan, RollbackTrigger.OnError ) );

		Assert.Equal(
			["-- deploy a/s1", "-- deploy b/s1", "-- rollback b/s1", "-- rollback a/s1"],
			_driver.AppliedScripts );
	}

	/// <summary>
	/// A plan whose transaction rolled back never reached the database, so compensating it would
	/// run a rollback script against a change that was never applied.
	/// </summary>
	[Fact]
	public void APlanTheOpenTransactionTookBackIsNotCompensated()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" )],
			[Plan( "a", "r1", "s1" ), Plan( "b", "r1", "s1" )] );

		_driver.FailingScripts.Add( "-- deploy b/s1" );

		Assert.Throws<StepScriptFailedException>(
			() => Deploy( manifest, DeploymentCommitLevel.Release, RollbackTrigger.OnError ) );

		// the whole release shared one transaction, so nothing committed and nothing is undone
		Assert.Empty( _driver.AppliedScripts );
		Assert.DoesNotContain( _listener.Events, x => x.StartsWith( "rolling-back" ) );
	}

	[Fact]
	public void OnSuccessUndoesTheWholeDeployment()
	{
		Deploy( TwoReleases(), DeploymentCommitLevel.Release, RollbackTrigger.OnSuccess );

		Assert.Equal(
			[
				"-- deploy a/s1", "-- deploy b/s1", "-- deploy c/s1",
				"-- rollback c/s1", "-- rollback b/s1", "-- rollback a/s1"
			],
			_driver.AppliedScripts );
	}

	[Fact]
	public void StepsWithinAPlanAreUndoneInReverse()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1", "s2", "s3" )] );

		Deploy( manifest, DeploymentCommitLevel.Release, RollbackTrigger.OnSuccess );

		Assert.Equal(
			["-- rollback a/s3", "-- rollback a/s2", "-- rollback a/s1"],
			_driver.AppliedScripts.Where( x => x.StartsWith( "-- rollback" ) ) );
	}

	/// <summary>
	/// Each release's rollback is bracketed by its own RollingBackRelease/RolledBackRelease, so a
	/// journal reading the entries back can tell one release's compensation from the next.
	/// </summary>
	[Fact]
	public void RollbackIsJournaledThroughItsOwnSlots()
	{
		Deploy( TwoReleases(), DeploymentCommitLevel.Release, RollbackTrigger.OnSuccess );

		List<string> journaled = _driver.Transactions.SelectMany( x => x.Journaled ).ToList();

		Assert.Equal(
			[
				"RollingBackRelease r2", "RollingBackPlan r2/c", "RollingBackStep r2/c/s1",
				"RolledBackStep r2/c/s1", "RolledBackPlan r2/c", "RolledBackRelease r2",
				"RollingBackRelease r1", "RollingBackPlan r1/b", "RollingBackStep r1/b/s1",
				"RolledBackStep r1/b/s1", "RolledBackPlan r1/b",
				"RollingBackPlan r1/a", "RollingBackStep r1/a/s1",
				"RolledBackStep r1/a/s1", "RolledBackPlan r1/a", "RolledBackRelease r1"
			],
			journaled.Where( x => x.Contains( "olledBack" ) || x.Contains( "ollingBack" ) ) );
	}

	[Fact]
	public void RollbackIsNarratedFromItsReasonToItsEnd()
	{
		Deploy( TwoReleases(), DeploymentCommitLevel.Release, RollbackTrigger.OnSuccess );

		Assert.Contains( "rolling-back deployment succeeded; rolling back as requested", _listener.Events );
		Assert.Equal(
			["rollback-plan r2/c", "rollback-plan r1/b", "rollback-plan r1/a"],
			_listener.Events.Where( x => x.StartsWith( "rollback-plan" ) ) );
		Assert.Equal( "rolled-back", _listener.Events.Last() );
	}

	[Fact]
	public void TheReasonNamesTheFailureThatTriggeredIt()
	{
		_driver.FailingScripts.Add( "-- deploy c/s1" );

		Assert.Throws<StepScriptFailedException>(
			() => Deploy( TwoReleases(), DeploymentCommitLevel.Release, RollbackTrigger.OnError ) );

		Assert.Contains( _listener.Events, x => x.StartsWith( "rolling-back deployment failed:" ) );
	}

	/// <summary>
	/// A half-compensated database is worse than either end state, so a failed rollback is louder
	/// than the failure that started it and replaces it — the reason was already reported.
	/// </summary>
	[Fact]
	public void AFailedRollbackScriptIsReportedAsSuchAndNamesTheStep()
	{
		_driver.FailingScripts.Add( "-- deploy c/s1" );
		_driver.FailingScripts.Add( "-- rollback b/s1" );

		RollbackFailedException error = Assert.Throws<RollbackFailedException>(
			() => Deploy( TwoReleases(), DeploymentCommitLevel.Release, RollbackTrigger.OnError ) );

		Assert.Equal( ( "r1", "b", "s1" ), (error.Release, error.Plan, error.Step) );
		Assert.Contains( "partially rolled back", error.Message );
	}

	[Fact]
	public void NothingToDeployRollsBackNothing()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" )] );

		_journal.Latest = new JournaledRelease( "r1", Completed: true );
		_journal.DeployedPlans["r1"] = ["a"];

		Deploy( manifest, DeploymentCommitLevel.Release, RollbackTrigger.OnSuccess );

		Assert.Empty( _driver.AppliedScripts );
		Assert.DoesNotContain( _listener.Events, x => x.StartsWith( "rolling-back" ) );
	}

	/// <summary>r1 holds 'a' then 'b', r2 holds 'c'; one step each.</summary>
	private static BundleManifest TwoReleases()
		=> Manifest(
			[Release( "r1" ), Release( "r2", "r1" )],
			[Plan( "a", "r1", "s1" ), Plan( "b", "r1", "s1" ), Plan( "c", "r2", "s1" )] );

	private void Deploy( BundleManifest manifest, DeploymentCommitLevel commitLevel, RollbackTrigger rollback )
	{
		_journal.Attach( _driver );

		new DeploymentEngine(
				_driver, FakeJournal.Statements(), ReaderFor( manifest, _layout ), _layout, commitLevel,
				_listener, rollback )
			.Deploy( manifest );
	}
}
