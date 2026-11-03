using Pug.Sqetch.Deployment;

using static Pug.Sqetch.Tests.Deployment.Lineages;

namespace Pug.Sqetch.Tests.Deployment;

/// <summary>
/// A step's verify script runs right after its deploy script, in the same transaction and before the
/// step is journaled as deployed, so a failing check fails the step like a failing deploy script.
/// </summary>
public class DeploymentVerificationTests
{
	private readonly InMemoryScripts _scripts = new();
	private readonly FakeDatabaseDriver _driver = new();
	private readonly RecordingListener _listener = new();
	private readonly FakeJournal _journal = new();

	[Fact]
	public void VerifyRunsAfterDeployAndBeforeTheStepIsJournaledInTheSameTransaction()
	{
		_scripts.Verify( "a", "s1", "-- verify a/s1" );

		Deploy( OnePlan(), DeploymentCommitLevel.Release, RollbackTrigger.None );

		List<string> relevant = _driver.Events
			.Where( x => x.StartsWith( "script " ) || x.Contains( "journal StepDeployed" ) )
			.Select( x => x.StartsWith( "script " ) ? x : "StepDeployed" )
			.ToList();

		Assert.Equal( [ "script #3 -- deploy a/s1", "script #3 -- verify a/s1", "StepDeployed" ], relevant );
		Assert.Equal( [ "-- deploy a/s1", "-- verify a/s1" ], _driver.AppliedScripts );
		Assert.Contains( "verifying r1/a/s1", _listener.Events );
	}

	[Theory]
	[InlineData( "" )]
	[InlineData( "  \n\t " )]
	public void ABlankVerifyScriptIsNotExecuted( string verify )
	{
		_scripts.Verify( "a", "s1", verify );

		Deploy( OnePlan(), DeploymentCommitLevel.Release, RollbackTrigger.None );

		Assert.Equal( [ "-- deploy a/s1" ], _driver.AppliedScripts );
		Assert.DoesNotContain( _listener.Events, x => x.StartsWith( "verifying" ) );
	}

	[Fact]
	public void AFailingVerifyFailsTheStepAndLeavesItUnjournaled()
	{
		_scripts.Verify( "a", "s1", "-- verify a/s1" );
		_driver.FailingScripts.Add( "-- verify a/s1" );

		StepVerificationFailedException error = Assert.Throws<StepVerificationFailedException>(
			() => Deploy( OnePlan(), DeploymentCommitLevel.Release, RollbackTrigger.None )
		);

		Assert.Equal( ( "r1", "a", "s1" ), ( error.Release, error.Plan, error.Step ) );
		Assert.IsType<InvalidOperationException>( error.InnerException );
		Assert.IsAssignableFrom<StepScriptFailedException>( error );

		Assert.DoesNotContain( _driver.Transactions.SelectMany( x => x.Journaled ), x => x.StartsWith( "StepDeployed" ) );
		Assert.DoesNotContain( _listener.Events, x => x.StartsWith( "step-done" ) );
		Assert.Contains( "verifying r1/a/s1", _listener.Events );
		Assert.Empty( _driver.AppliedScripts );
		Assert.True( _driver.Transactions[^1].RolledBack );
	}

	[Fact]
	public void AtReleaseLevelAFailingVerifyCommitsNothing()
	{
		VerifyFailsInSecondPlan();

		Assert.Throws<StepVerificationFailedException>(
			() => Deploy( TwoPlans(), DeploymentCommitLevel.Release, RollbackTrigger.None )
		);

		Assert.Empty( _driver.AppliedScripts );
		Assert.DoesNotContain( "committed", _listener.Events );
	}

	[Fact]
	public void AtReleaseLevelOnErrorHasNothingToCompensate()
	{
		VerifyFailsInSecondPlan();

		Assert.Throws<StepVerificationFailedException>(
			() => Deploy( TwoPlans(), DeploymentCommitLevel.Release, RollbackTrigger.OnError )
		);

		Assert.Empty( _driver.AppliedScripts );
		Assert.DoesNotContain( _listener.Events, x => x.StartsWith( "rolling-back" ) );
	}

	[Fact]
	public void AtPlanLevelTheEarlierPlanStaysCommittedWithoutOnError()
	{
		VerifyFailsInSecondPlan();

		Assert.Throws<StepVerificationFailedException>(
			() => Deploy( TwoPlans(), DeploymentCommitLevel.Plan, RollbackTrigger.None )
		);

		Assert.Equal( [ "-- deploy a/s1", "-- verify a/s1" ], _driver.AppliedScripts );
		Assert.Contains( "committed", _listener.Events );
	}

	[Fact]
	public void AtPlanLevelOnErrorCompensatesTheEarlierPlan()
	{
		VerifyFailsInSecondPlan();

		Assert.Throws<StepVerificationFailedException>(
			() => Deploy( TwoPlans(), DeploymentCommitLevel.Plan, RollbackTrigger.OnError )
		);

		Assert.Equal( [ "-- deploy a/s1", "-- verify a/s1", "-- rollback a/s1" ], _driver.AppliedScripts );
		Assert.Contains( "rolled-back", _listener.Events );
	}

	[Fact]
	public void WhereDdlCommitsItselfOnErrorCompensatesTheStepWhoseVerifyFailed()
	{
		_driver.CommitsDdlImplicitly = true;
		VerifyFailsInSecondPlan();

		Assert.Throws<StepVerificationFailedException>(
			() => Deploy( TwoPlans(), DeploymentCommitLevel.Plan, RollbackTrigger.OnError )
		);

		Assert.Equal(
			[ "-- deploy a/s1", "-- verify a/s1", "-- rollback b/s1", "-- rollback a/s1" ],
			_driver.AppliedScripts
		);
		Assert.Contains( "committed", _listener.Events );
		Assert.Contains( "rolled-back", _listener.Events );
	}

	[Fact]
	public void WithVerificationOffNoVerifyScriptIsExecuted()
	{
		_scripts.Verify( "a", "s1", "-- verify a/s1" );
		_driver.FailingScripts.Add( "-- verify a/s1" );

		Deploy( OnePlan(), DeploymentCommitLevel.Release, RollbackTrigger.None, verifySteps: false );

		Assert.Equal( [ "-- deploy a/s1" ], _driver.AppliedScripts );
		Assert.DoesNotContain( _listener.Events, x => x.StartsWith( "verifying" ) );
	}

	/// <summary>Plan 'a' verifies; plan 'b' deploys and then fails its verify.</summary>
	private void VerifyFailsInSecondPlan()
	{
		_scripts.Verify( "a", "s1", "-- verify a/s1" );
		_scripts.Verify( "b", "s1", "-- verify b/s1" );
		_driver.FailingScripts.Add( "-- verify b/s1" );
	}

	private static IReadOnlyList<ReleaseGroup> OnePlan()
		=> Lineage( [ Release( "r1" ) ], [ Plan( "a", "r1", "s1" ) ] );

	private static IReadOnlyList<ReleaseGroup> TwoPlans()
		=> Lineage( [ Release( "r1" ) ], [ Plan( "a", "r1", "s1" ), Plan( "b", "r1", "s1" ) ] );

	private void Deploy(
		IReadOnlyList<ReleaseGroup> releases,
		DeploymentCommitLevel commitLevel,
		RollbackTrigger rollback,
		bool verifySteps = true
	)
	{
		_journal.Attach( _driver );

		new DeploymentEngine( _driver, FakeJournal.Statements(), _scripts, commitLevel, _listener, rollback )
			.Deploy( Project, releases, verifySteps );
	}
}
