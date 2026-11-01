using Pug.Sqetch.Deployment.Bundling;
using Pug.Sqetch.Deployment.Bundling.Layouts;
using Pug.Sqetch.Deployment;
using static Pug.Sqetch.Tests.Deployment.Manifests;

namespace Pug.Sqetch.Tests.Deployment;

/// <summary>
/// The result of a deployment is what it left behind: the journal state a later run reads back
/// (<see cref="FakeJournal.Latest"/>, <see cref="FakeJournal.DeployedPlans"/>,
/// <see cref="FakeJournal.StartedReleases"/>) and the step scripts the database kept
/// (<see cref="FakeDatabaseDriver.AppliedScripts"/>). Tests assert that, so a change to how the
/// engine narrates its progress cannot fail them.
///
/// The engine journals by running the project's SQL, so these tests bundle marker statements and
/// <see cref="FakeJournal"/> interprets them — there is no journal-writer abstraction to fake.
/// Every deployment opens two transactions before any of its own: one for the journal's schema and
/// one for the two journal queries. Deployment transactions therefore start at #3, and a run that
/// finds nothing to deploy still opens those two.
///
/// Three things are asserted separately, each in its own section below, because they are separate
/// contracts rather than incidental detail: what the transactions guarantee, what one transaction
/// makes durable together, and what the host is told.
/// </summary>
public class DeploymentEngineTests
{
	private readonly DefaultBundleLayout _layout = new ();
	private readonly FakeDatabaseDriver _driver = new ();
	private readonly RecordingListener _listener = new ();
	private readonly FakeJournal _journal = new ();

	// ---------------------------------------------------------------- what gets deployed

	[Fact]
	public void FreshDatabaseDeploysEveryPlanOfEveryRelease()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" ), Release( "r2", "r1" )],
			[Plan( "alpha", "r1", "s1", "s2" ), Plan( "beta", "r1", "s1" ), Plan( "gamma", "r2", "s1" )] );

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.Equal(
			["-- deploy alpha/s1", "-- deploy alpha/s2", "-- deploy beta/s1", "-- deploy gamma/s1"],
			_driver.AppliedScripts );

		Assert.Equal( ["r1", "r2"], _journal.StartedReleases );
		Assert.Equal( ["alpha", "beta"], Deployed( "r1" ) );
		Assert.Equal( ["gamma"], Deployed( "r2" ) );
		Assert.Equal( new JournaledRelease( "r2", Completed: true ), _journal.Latest );
	}

	[Fact]
	public void ResumingAnIncompleteReleaseDeploysOnlyItsUndeployedPlansThenContinues()
	{
		BundleManifest manifest = Manifest(
			[Release( "r0" ), Release( "r1", "r0" ), Release( "r2", "r1" )],
			[Plan( "p0", "r0", "s1" ), Plan( "a", "r1", "s1" ), Plan( "b", "r1", "s1" ), Plan( "c", "r2", "s1" )] );

		_journal.Latest = new JournaledRelease( "r1", Completed: false );
		_journal.DeployedPlans["r1"] = ["a"];

		Deploy( manifest, DeploymentCommitLevel.Release );

		// r0 is behind the resume point and r1/a is already deployed, so neither runs again
		Assert.Equal( ["-- deploy b/s1", "-- deploy c/s1"], _driver.AppliedScripts );
		Assert.Equal( ["a", "b"], Deployed( "r1" ) );
		Assert.Equal( ["c"], Deployed( "r2" ) );
		Assert.Equal( new JournaledRelease( "r2", Completed: true ), _journal.Latest );
	}

	/// <summary>
	/// A resumed release's start is already on record — that is what made it the latest release —
	/// so journaling it again would write a second start for one deployment of it.
	/// </summary>
	[Fact]
	public void AResumedReleaseDoesNotHaveItsStartJournaledAgain()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" ), Release( "r2", "r1" )],
			[Plan( "a", "r1", "s1" ), Plan( "b", "r1", "s1" ), Plan( "c", "r2", "s1" )] );

		_journal.Latest = new JournaledRelease( "r1", Completed: false );
		_journal.DeployedPlans["r1"] = ["a"];

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.Equal( ["r2"], _journal.StartedReleases );
	}

	/// <summary>
	/// A release's plans are only partly ordered — two disjoint dependency chains (here a → b and
	/// c → d) can be listed in any interleaving, and nothing the journal records pins which one a
	/// past run used. Resume therefore skips the plans the journal names wherever they sit, rather
	/// than everything positioned before the last of them: by position, listing the journaled
	/// plans out of their deployed order either redeploys one or silently skips an undeployed one
	/// and then journals the release complete.
	/// </summary>
	[Fact]
	public void ResumeSkipsTheJournaledPlansWhereverTheBundleListsThem()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" )],
			[
				Plan( "c", "r1", "s1" ), Plan( "a", "r1", "s1" ),
				Plan( "b", "r1", "s1" ), Plan( "d", "r1", "s1" )
			] );

		// both chain heads are deployed, and the journal reports them in an order the bundle does
		// not share, which it is not required to: 'c' is last here but first in the bundle, so
		// resuming at the plan after it would redeploy 'a'
		_journal.Latest = new JournaledRelease( "r1", Completed: false );
		_journal.DeployedPlans["r1"] = ["a", "c"];

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.Equal( ["-- deploy b/s1", "-- deploy d/s1"], _driver.AppliedScripts );
		Assert.Equal( ["a", "c", "b", "d"], Deployed( "r1" ) );
		Assert.Equal( new JournaledRelease( "r1", Completed: true ), _journal.Latest );
	}

	[Fact]
	public void AnIncompleteReleaseWithNoDeployedPlanDeploysFromItsFirstPlan()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" ), Release( "r2", "r1" )],
			[Plan( "a", "r1", "s1" ), Plan( "b", "r2", "s1" )] );

		// journaled as started with nothing committed, which only a writer journaling outside
		// the deployment transaction can produce
		_journal.Latest = new JournaledRelease( "r1", Completed: false );

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.Equal( ["-- deploy a/s1", "-- deploy b/s1"], _driver.AppliedScripts );
		Assert.Equal( ["a"], Deployed( "r1" ) );
		Assert.Equal( ["r2"], _journal.StartedReleases );
	}

	/// <summary>
	/// Journaling the completion of a release whose every plan is already deployed closes one that
	/// would otherwise stay incomplete forever and block every later bundle.
	/// </summary>
	[Fact]
	public void AnIncompleteReleaseWhoseEveryPlanIsDeployedIsCompletedWithoutRunningAScript()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" ), Release( "r2", "r1" )],
			[Plan( "a", "r1", "s1" ), Plan( "b", "r2", "s1" )] );

		_journal.Latest = new JournaledRelease( "r1", Completed: false );
		_journal.DeployedPlans["r1"] = ["a"];

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.Equal( ["-- deploy b/s1"], _driver.AppliedScripts );
		Assert.Equal( ["a"], Deployed( "r1" ) );
		Assert.Equal( new JournaledRelease( "r2", Completed: true ), _journal.Latest );
	}

	[Fact]
	public void ACompletelyDeployedReleaseHandsOverToTheReleaseDependingOnIt()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" ), Release( "r2", "r1" )],
			[Plan( "a", "r1", "s1" ), Plan( "b", "r2", "s1" )] );

		_journal.Latest = new JournaledRelease( "r1", Completed: true );
		_journal.DeployedPlans["r1"] = ["a"];

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.Equal( ["-- deploy b/s1"], _driver.AppliedScripts );
		Assert.Equal( ["r2"], _journal.StartedReleases );
		Assert.Equal( new JournaledRelease( "r2", Completed: true ), _journal.Latest );
	}

	/*
	/// <summary>
	/// A completed release is handed over from, never resumed into, so plans a bundle gained for
	/// one are not deployed. Only an open release can gain plans — finalized ones are frozen —
	/// and a database that has deployed unreleased plans is single-use by design: the next test
	/// deployment runs against a restored backup.
	/// </summary>
	[Fact]
	public void ACompletedReleaseIsNotRevisitedEvenWhenTheBundleGainedPlansForIt()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" )],
			[Plan( "a", "r1", "s1" ), Plan( "b", "r1", "s1" )] );

		_journal.Latest = new JournaledRelease( "r1", Completed: true );
		_journal.DeployedPlans["r1"] = ["a"];

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.Empty( _driver.AppliedScripts );
		Assert.Equal( ["a"], Deployed( "r1" ) );
		Assert.Equal( new JournaledRelease( "r1", Completed: true ), _journal.Latest );
	}
	*/

	/// <summary>
	/// The journal is prepared and read on every deployment, so an up-to-date database opens those
	/// two transactions and no more — nothing is deployed and nothing else is touched.
	/// </summary>
	[Fact]
	public void RedeployingAnUnchangedBundleChangesNothingBeyondPreparingAndReadingTheJournal()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" )] );

		_journal.Latest = new JournaledRelease( "r1", Completed: true );
		_journal.DeployedPlans["r1"] = ["a"];

		Deploy( manifest, DeploymentCommitLevel.Release );

		AssertNothingWasDeployed( 2 );
		Assert.Equal( new JournaledRelease( "r1", Completed: true ), _journal.Latest );
	}

	[Fact]
	public void ContinuationBundleFollowingACompletelyDeployedReleaseIsDeployedWhole()
	{
		BundleManifest manifest = Manifest( [Release( "r2", "r1" )], [Plan( "c", "r2", "s1" )] );

		_journal.Latest = new JournaledRelease( "r1", Completed: true );

		// the bundle's own release is not the journaled one, so its deployed plans are never
		// consulted: 'c' deploys even though the journal already claims it
		_journal.DeployedPlans["r2"] = ["c"];

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.Equal( ["-- deploy c/s1"], _driver.AppliedScripts );
		Assert.Equal( ["r2"], _journal.StartedReleases );
		Assert.Equal( new JournaledRelease( "r2", Completed: true ), _journal.Latest );
	}

	[Fact]
	public void UnreleasedPlansDeployLastAsThePseudoRelease()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" )],
			[Plan( "a", "r1", "s1" ), Plan( "z", "", "s1" )] );

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.Equal( ["-- deploy a/s1", "-- deploy z/s1"], _driver.AppliedScripts );
		Assert.Equal( ["r1", ""], _journal.StartedReleases );
		Assert.Equal( ["z"], Deployed( "" ) );
		Assert.Equal( new JournaledRelease( "", Completed: true ), _journal.Latest );
	}

	[Fact]
	public void UnreleasedPlansAreReachedThroughTheDependencyChain()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" )],
			[Plan( "a", "r1", "s1" ), Plan( "z", "", "s1" )] );

		_journal.Latest = new JournaledRelease( "r1", Completed: true );
		_journal.DeployedPlans["r1"] = ["a"];

		Deploy( manifest, DeploymentCommitLevel.Release );

		// the pseudo-release depends on the last real release, so it is what follows r1
		Assert.Equal( ["-- deploy z/s1"], _driver.AppliedScripts );
		Assert.Equal( [""], _journal.StartedReleases );
		Assert.Equal( new JournaledRelease( "", Completed: true ), _journal.Latest );
	}

	[Fact]
	public void ResumesInsideThePseudoRelease()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" )],
			[Plan( "a", "r1", "s1" ), Plan( "y", "", "s1" ), Plan( "z", "", "s1" )] );

		_journal.Latest = new JournaledRelease( "", Completed: false );
		_journal.DeployedPlans[""] = ["y"];

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.Equal( ["-- deploy z/s1"], _driver.AppliedScripts );
		Assert.Equal( ["y", "z"], Deployed( "" ) );
		Assert.Empty( _journal.StartedReleases );
		Assert.Equal( new JournaledRelease( "", Completed: true ), _journal.Latest );
	}

	/// <summary>
	/// Nothing the failed transaction wrote survives, so the journal still reads as it did after
	/// the last boundary commit — the next run resumes from there rather than from a release it
	/// only appears to have started.
	/// </summary>
	[Fact]
	public void StepFailureLeavesTheJournalAtTheLastCommittedRelease()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" ), Release( "r2", "r1" )],
			[Plan( "a", "r1", "s1" ), Plan( "b", "r2", "s1" )] );

		_driver.FailingScripts.Add( "-- deploy b/s1" );

		Assert.Throws<StepScriptFailedException>( () => Deploy( manifest, DeploymentCommitLevel.Release ) );

		Assert.Equal( ["-- deploy a/s1"], _driver.AppliedScripts );
		Assert.Equal( ["r1"], _journal.StartedReleases );
		Assert.Equal( ["a"], Deployed( "r1" ) );
		Assert.Empty( Deployed( "r2" ) );
		Assert.Equal( new JournaledRelease( "r1", Completed: true ), _journal.Latest );
	}

	// ---------------------------------------------------------------- what gets refused

	[Fact]
	public void JournaledPlansMissingFromTheBundleAreRefused()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" )] );

		// the bundle was rebuilt without plans the database has deployed, so it is not the same
		// release and nothing in it can be resumed
		_journal.Latest = new JournaledRelease( "r1", Completed: false );
		_journal.DeployedPlans["r1"] = ["a", "scrapped", "renamed"];

		IncompatibleBundleException error = Assert.Throws<IncompatibleBundleException>(
			() => Deploy( manifest, DeploymentCommitLevel.Release ) );

		Assert.Contains( "deployed plans of release 'r1'", error.Message );
		Assert.Contains( "'scrapped', 'renamed'", error.Message );
		Assert.Contains( "restore the database", error.Message );
		AssertNothingWasDeployed( 2 );
	}

	/// <summary>
	/// Nothing can depend on the pseudo-release, so a bundle that starts the lineage must not be
	/// matched to it through their shared empty dependency and redeployed over a test database.
	/// </summary>
	[Fact]
	public void ADatabaseLeftAtThePseudoReleaseCannotBeFollowedByALineageStartingBundle()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" )] );

		_journal.Latest = new JournaledRelease( "", Completed: true );

		IncompatibleBundleException error = Assert.Throws<IncompatibleBundleException>(
			() => Deploy( manifest, DeploymentCommitLevel.Release ) );

		Assert.Contains( "pseudo", error.Message );
		AssertNothingWasDeployed( 2 );
	}

	[Fact]
	public void ContinuationBundleIsRefusedWhenThePredecessorIsOnlyPartiallyDeployed()
	{
		BundleManifest manifest = Manifest( [Release( "r2", "r1" )], [Plan( "c", "r2", "s1" )] );

		_journal.Latest = new JournaledRelease( "r1", Completed: false );

		IncompatibleBundleException error = Assert.Throws<IncompatibleBundleException>(
			() => Deploy( manifest, DeploymentCommitLevel.Release ) );

		Assert.Contains( "release 'r1' is only partially deployed", error.Message );
		Assert.Contains( "release 'r2'", error.Message );
		Assert.Equal( new JournaledRelease( "r1", Completed: false ), error.Journaled );
		AssertNothingWasDeployed( 2 );
	}

	[Fact]
	public void ContinuationBundleIsRefusedOnAFreshDatabase()
	{
		BundleManifest manifest = Manifest( [Release( "r2", "r1" )], [Plan( "c", "r2", "s1" )] );

		IncompatibleBundleException error = Assert.Throws<IncompatibleBundleException>(
			() => Deploy( manifest, DeploymentCommitLevel.Release ) );

		Assert.Contains( "continues from release 'r1'", error.Message );
		Assert.Contains( "no deployed releases", error.Message );
		Assert.Null( error.Journaled );
		AssertNothingWasDeployed( 2 );
	}

	[Fact]
	public void JournaledReleaseTheBundleNeitherContainsNorContinuesFromIsRefused()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" )] );

		_journal.Latest = new JournaledRelease( "r9", Completed: true );

		IncompatibleBundleException error = Assert.Throws<IncompatibleBundleException>(
			() => Deploy( manifest, DeploymentCommitLevel.Release ) );

		Assert.Equal(
			"database is at release 'r9', which this bundle neither contains nor continues from.",
			error.Message );
		Assert.Equal( "r9", error.Journaled!.Name );
		AssertNothingWasDeployed( 2 );
	}

	[Fact]
	public void ReleasesThatDoNotFormAContiguousChainAreRefused()
	{
		BundleManifest manifest = Manifest(
			[Release( "r0" ), Release( "r2", "r1" )],
			[Plan( "a", "r0", "s1" ), Plan( "c", "r2", "s1" )] );

		InvalidBundleException error = Assert.Throws<InvalidBundleException>(
			() => Deploy( manifest, DeploymentCommitLevel.Release ) );

		Assert.Equal(
			"Release 'r2' does not depend on 'r0', the release preceding it in the bundle.",
			error.Message );
		AssertNothingWasDeployed( 0 );
	}

	[Fact]
	public void PlanReferencingAnUnlistedReleaseIsRefused()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r9", "s1" )] );

		InvalidBundleException error = Assert.Throws<InvalidBundleException>(
			() => Deploy( manifest, DeploymentCommitLevel.Release ) );

		Assert.Contains( "'r9'", error.Message );
		AssertNothingWasDeployed( 0 );
	}

	// ------------------------------------------------- what the transactions guarantee

	[Fact]
	public void ReleaseCommitLevelCommitsOncePerRelease()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" ), Release( "r2", "r1" )],
			[Plan( "alpha", "r1", "s1" ), Plan( "beta", "r2", "s1" )] );

		Deploy( manifest, DeploymentCommitLevel.Release );

		// the journal's schema and its two queries, then one transaction per release
		Assert.Equal( 4, _driver.Transactions.Count );
		Assert.All( _driver.Transactions, transaction => Assert.True( transaction.Committed ) );

		// each release's whole deployment, completion included, is one transaction
		Assert.Contains( "ReleaseDeployed r1", _driver.Transactions[2].Journaled );
		Assert.Contains( "PlanDeployed r1/alpha", _driver.Transactions[2].Journaled );
		Assert.Contains( "ReleaseDeployed r2", _driver.Transactions[3].Journaled );
		Assert.Contains( "PlanDeployed r2/beta", _driver.Transactions[3].Journaled );
	}

	/// <summary>
	/// A release's completion must never land in a transaction of its own: were the last plan's
	/// commit to survive and the completion's not, the release would stay incomplete with every
	/// plan deployed.
	/// </summary>
	[Fact]
	public void PlanCommitLevelCommitsBetweenPlansAndJournalsTheReleaseWithItsLastPlan()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" )],
			[Plan( "alpha", "r1", "s1" ), Plan( "beta", "r1", "s1" )] );

		Deploy( manifest, DeploymentCommitLevel.Plan );

		// the journal's schema and its two queries, then one transaction per plan
		Assert.Equal( 4, _driver.Transactions.Count );
		Assert.All( _driver.Transactions, transaction => Assert.True( transaction.Committed ) );

		Assert.Contains( "PlanDeployed r1/alpha", _driver.Transactions[2].Journaled );
		Assert.DoesNotContain( "ReleaseDeployed r1", _driver.Transactions[2].Journaled );

		Assert.Contains( "PlanDeployed r1/beta", _driver.Transactions[3].Journaled );
		Assert.Contains( "ReleaseDeployed r1", _driver.Transactions[3].Journaled );
	}

	[Fact]
	public void StepFailureRollsBackTheOpenTransactionAndKeepsEarlierCommits()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" ), Release( "r2", "r1" )],
			[Plan( "a", "r1", "s1" ), Plan( "b", "r2", "s1" )] );

		_driver.FailingScripts.Add( "-- deploy b/s1" );

		StepScriptFailedException error = Assert.Throws<StepScriptFailedException>(
			() => Deploy( manifest, DeploymentCommitLevel.Release ) );

		Assert.Equal( ( "r2", "b", "s1" ), (error.Release, error.Plan, error.Step) );

		Assert.True( _driver.Transactions[2].Committed );
		Assert.True( _driver.Transactions[3].RolledBack );
		Assert.False( _driver.Transactions[3].Committed );
		Assert.Equal( "rollback #4", _driver.Events.Last() );
	}

	/// <summary>
	/// The one test that pins the exact sequence, because three things have to be true together and
	/// nothing else asserts them: the journal's schema is committed alone before the journal is read,
	/// both queries share one transaction of their own, and a slot's statement can sit between a
	/// step's start and its completion. Everything else asserts what the deployment left behind.
	/// Parameters are spelled out here once; <c>StatementJournalTests</c> covers them per slot.
	/// </summary>
	[Fact]
	public void TheJournalIsPreparedThenReadThenInterleavedWithTheScripts()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" )],
			[Plan( "alpha", "r1", "s1", "s2" ), Plan( "beta", "r1", "s1" )] );

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.Equal(
			[
				"begin #1",
				"journal-statement #1 journal PrepareJournal [project=demo]",
				"commit #1",
				"begin #2",
				"journal-query #2 journal GetLatestRelease [project=demo]",
				"commit #2",
				"begin #3",
				"journal-statement #3 journal DeployingRelease [project=demo, release=r1, description=, utcTimestamp=<utc>]",
				"journal-statement #3 journal DeployingPlan [project=demo, release=r1, plan=alpha, description=, utcTimestamp=<utc>]",
				"journal-statement #3 journal DeployingStep [project=demo, release=r1, plan=alpha, step=s1, description=, utcTimestamp=<utc>]",
				"script #3 -- deploy alpha/s1",
				"journal-statement #3 journal StepDeployed [project=demo, release=r1, plan=alpha, step=s1, description=, utcTimestamp=<utc>]",
				"journal-statement #3 journal DeployingStep [project=demo, release=r1, plan=alpha, step=s2, description=, utcTimestamp=<utc>]",
				"script #3 -- deploy alpha/s2",
				"journal-statement #3 journal StepDeployed [project=demo, release=r1, plan=alpha, step=s2, description=, utcTimestamp=<utc>]",
				"journal-statement #3 journal PlanDeployed [project=demo, release=r1, plan=alpha, description=, utcTimestamp=<utc>]",
				"journal-statement #3 journal DeployingPlan [project=demo, release=r1, plan=beta, description=, utcTimestamp=<utc>]",
				"journal-statement #3 journal DeployingStep [project=demo, release=r1, plan=beta, step=s1, description=, utcTimestamp=<utc>]",
				"script #3 -- deploy beta/s1",
				"journal-statement #3 journal StepDeployed [project=demo, release=r1, plan=beta, step=s1, description=, utcTimestamp=<utc>]",
				"journal-statement #3 journal PlanDeployed [project=demo, release=r1, plan=beta, description=, utcTimestamp=<utc>]",
				"journal-statement #3 journal ReleaseDeployed [project=demo, release=r1, description=, utcTimestamp=<utc>]",
				"commit #3"
			],
			_driver.Events );
	}

	/// <summary>
	/// The journal's schema must exist before the journal can be read, and DDL auto-commits on some
	/// engines, so sharing a transaction with the reads would implicitly end it part-way.
	/// </summary>
	[Fact]
	public void PreparingTheJournalCommitsOnItsOwnBeforeTheQueriesRun()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" )] );

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.Equal( ["PrepareJournal"], _driver.Transactions[0].Journaled );
		Assert.True( _driver.Transactions[0].Committed );

		// the reads are their own transaction, so the two queries cannot disagree
		Assert.Equal(
			[( "journal GetLatestRelease" )],
			_driver.Transactions[1].JournalingCalls.Select( call => call.Statement ) );
	}

	/// <summary>
	/// A deployment that read a resumable release runs both queries, and both in the read
	/// transaction — never one there and one later.
	/// </summary>
	[Fact]
	public void BothJournalQueriesShareOneTransaction()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" )],
			[Plan( "a", "r1", "s1" ), Plan( "b", "r1", "s1" )] );

		_journal.Latest = new JournaledRelease( "r1", Completed: false );
		_journal.DeployedPlans["r1"] = ["a"];

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.Equal(
			["journal GetLatestRelease", "journal GetDeployedPlans"],
			_driver.Transactions[1].JournalingCalls.Select( call => call.Statement ) );
		Assert.True( _driver.Transactions[1].Committed );
	}

	// -------------------------------------------------------- what the host is told
	//
	// The listener is the CLI's progress channel and a deliverable in its own right, so it is
	// asserted here and nowhere else. A test about what was deployed must not fail because the
	// narration changed, and narration that misreports what happened must still fail something.

	[Fact]
	public void DeploymentIsNarratedInFull()
	{
		BundleManifest manifest = Manifest( [Release( "r2", "r1" )], [Plan( "c", "r2", "s1" )] );

		_journal.Latest = new JournaledRelease( "r1", Completed: true );

		Deploy( manifest, DeploymentCommitLevel.Release );

		// a continuation is announced once — nothing is skipped, so it is the only indication of
		// what the deployment builds on
		Assert.Equal(
			[
				"continuing r1",
				"release r2",
				"plan r2/c",
				"step r2/c/s1",
				"step-done r2/c/s1",
				"plan-done r2/c",
				"release-done r2",
				"committed"
			],
			_listener.Events );
	}

	[Fact]
	public void ReleasesBehindTheResumePointAndAlreadyDeployedPlansAreReportedAsSkipped()
	{
		BundleManifest manifest = Manifest(
			[Release( "r0" ), Release( "r1", "r0" )],
			[Plan( "p0", "r0", "s1" ), Plan( "a", "r1", "s1" ), Plan( "b", "r1", "s1" )] );

		_journal.Latest = new JournaledRelease( "r1", Completed: false );
		_journal.DeployedPlans["r1"] = ["a"];

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.Contains( "skip-release r0", _listener.Events );
		Assert.Contains( "skip-plan r1/a", _listener.Events );
	}

	/// <summary>
	/// The release deployment resumes into is being deployed, not skipped: reporting it as skipped
	/// would tell an operator the opposite of what happened.
	/// </summary>
	[Fact]
	public void TheReleaseBeingResumedIntoIsNotReportedAsSkipped()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" )],
			[Plan( "a", "r1", "s1" ), Plan( "b", "r1", "s1" )] );

		_journal.Latest = new JournaledRelease( "r1", Completed: false );
		_journal.DeployedPlans["r1"] = ["a"];

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.DoesNotContain( "skip-release r1", _listener.Events );
		Assert.Contains( "plan-done r1/b", _listener.Events );
	}

	/// <summary>
	/// A release the bundle contains is resumed into rather than continued from, so the
	/// continuation announcement would be misleading.
	/// </summary>
	[Fact]
	public void AResumedReleaseIsNotAnnouncedAsAContinuation()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" ), Release( "r2", "r1" )],
			[Plan( "a", "r1", "s1" ), Plan( "b", "r1", "s1" ), Plan( "c", "r2", "s1" )] );

		_journal.Latest = new JournaledRelease( "r1", Completed: false );
		_journal.DeployedPlans["r1"] = ["a"];

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.DoesNotContain( "continuing r1", _listener.Events );
	}

	[Fact]
	public void AnUpToDateDatabaseIsReportedAsNothingToDeploy()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" )] );

		_journal.Latest = new JournaledRelease( "r1", Completed: true );
		_journal.DeployedPlans["r1"] = ["a"];

		Deploy( manifest, DeploymentCommitLevel.Release );

		Assert.Equal( ["skip-release r1", "nothing"], _listener.Events );
	}

	[Fact]
	public void ARefusedBundleIsNotNarratedAtAll()
	{
		BundleManifest manifest = Manifest( [Release( "r2", "r1" )], [Plan( "c", "r2", "s1" )] );

		Assert.Throws<IncompatibleBundleException>( () => Deploy( manifest, DeploymentCommitLevel.Release ) );

		Assert.Empty( _listener.Events );
	}

	// ---------------------------------------------------------------------------- support

	private IReadOnlyList<string> Deployed( string release )
		=> _journal.DeployedPlans.TryGetValue( release, out List<string>? plans ) ? plans : [];

	/// <summary>
	/// A refused bundle changes nothing: no script ran and the journal gained no release.
	/// <paramref name="transactions"/> says how far the refusal got — 0 for a malformed manifest,
	/// refused before the database is touched at all, and 2 where the refusal depends on what the
	/// journal says, which means the journal was necessarily prepared and read first.
	/// </summary>
	private void AssertNothingWasDeployed( int transactions )
	{
		Assert.Equal( transactions, _driver.Transactions.Count );
		Assert.Empty( _driver.AppliedScripts );
		Assert.Empty( _journal.StartedReleases );
	}

	private void Deploy( BundleManifest manifest, DeploymentCommitLevel commitLevel )
	{
		_journal.Attach( _driver );

		new DeploymentEngine(
				_driver, FakeJournal.Statements(), ReaderFor( manifest, _layout ), _layout, commitLevel,
				_listener )
			.Deploy( manifest );
	}
}
