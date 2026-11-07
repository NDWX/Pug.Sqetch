using System.Globalization;

using Pug.Sqetch.Deployment;
using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Tests.Deployment;

using static Pug.Sqetch.Tests.Deployment.Lineages;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// The deployment scenarios worth running against a real server, written once and run by every
/// provider's test class. Everything else asserts the deployment flow against a fake that answers
/// the journal queries from seeded state; these are the ones where the resume decision is read back
/// out of SQL the deployment itself wrote, so a provider's own answer to 'what did my journal say'
/// is what drives the engine.
///
/// A class rather than a base class: a provider that needs Docker gates its facts with its own
/// attribute, which an inherited <c>[Fact]</c> could not do.
/// </summary>
/// <param name="driver">
/// A driver onto the same database each time it is called — a deployment opens and releases its own,
/// and these scenarios deploy more than once.
/// </param>
/// <param name="journaling">The provider's journaling SQL, which is itself part of what is tested.</param>
/// <param name="query">Reads committed rows back, outside the deployment's connection.</param>
/// <param name="stepTables">
/// The tables the step scripts created, in name order — asked of the provider rather than derived
/// from one catalog query, since SQLite has no information_schema.
/// </param>
/// <param name="identifier">
/// Quotes a journal column name for reading the journal back: 'release' and 'plan' are reserved on
/// some engines ('RELEASE' in MySQL, 'PLAN' in T-SQL), and their templates quote them too. Names
/// are used as they are when omitted.
/// </param>
/// <param name="endPooledSessions">
/// Closes the sessions a provider keeps pooled once a driver is disposed, so that a lock only a
/// session end would free is really freed: a pool may hand a connection back with its session state
/// intact until it is next used (Npgsql resets lazily). The 'holder disposed' step therefore proves
/// the lock follows its session, not that a pool resets on close. Nothing when omitted.
/// </param>
public sealed class RealDatabaseDeployment(
	Func<IDatabaseDriver> driver,
	JournalingStatements journaling,
	Func<string, List<string>> query,
	Func<List<string>> stepTables,
	Func<string, string>? identifier = null,
	Action? endPooledSessions = null
)
{
	private string Column( string name ) => identifier?.Invoke( name ) ?? name;

	/// <summary>Two releases, three plans, one step each; every step creates its own table.</summary>
	private static IReadOnlyList<ReleaseGroup> TwoReleases()
		=> Lineage(
			[ Release( "r1" ), Release( "r2", "r1" ) ],
			[ Plan( "a", "r1", "s1" ), Plan( "b", "r1", "s1" ), Plan( "c", "r2", "s1" ) ]
		);

	public void AWholeBundleIsAppliedAndJournaled()
	{
		IReadOnlyList<ReleaseGroup> releases = TwoReleases();

		Deploy( releases, Scripts() );

		Assert.Equal( [ "a_s1", "b_s1", "c_s1" ], StepTables() );

		Assert.Equal(
			[
				"DeployingRelease|r1||",
				"DeployingPlan|r1|a|",
				"DeployingStep|r1|a|s1",
				"StepDeployed|r1|a|s1",
				"PlanDeployed|r1|a|",
				"DeployingPlan|r1|b|",
				"DeployingStep|r1|b|s1",
				"StepDeployed|r1|b|s1",
				"PlanDeployed|r1|b|",
				"ReleaseDeployed|r1||",
				"DeployingRelease|r2||",
				"DeployingPlan|r2|c|",
				"DeployingStep|r2|c|s1",
				"StepDeployed|r2|c|s1",
				"PlanDeployed|r2|c|",
				"ReleaseDeployed|r2||"
			],
			Journal()
		);
	}

	/// <summary>
	/// The parameter exists for the engine that cannot supply the instant itself, so what matters is
	/// that the host's clock is what reached the row — not merely that the column is populated.
	/// </summary>
	public void TheHostsUtcInstantIsWhatTheJournalRecords()
	{
		DateTime before = DateTime.UtcNow.AddSeconds( -5 );

		IReadOnlyList<ReleaseGroup> releases = TwoReleases();

		Deploy( releases, Scripts() );

		List<DateTime> instants = query( "select at_utc from sqetch_journal order by id" )
			.Select( at => DateTime.Parse(
					at,
					CultureInfo.InvariantCulture,
					DateTimeStyles.AdjustToUniversal
				)
			)
			.ToList();

		Assert.Equal( 16, instants.Count );
		Assert.All( instants, instant => Assert.InRange( instant, before, DateTime.UtcNow.AddSeconds( 5 ) ) );

		// the clock is read per event, so a release's start cannot be stamped after its completion
		Assert.Equal( instants.OrderBy( instant => instant ), instants );
	}

	public void RedeployingTheSameBundleDeploysNothing()
	{
		IReadOnlyList<ReleaseGroup> releases = TwoReleases();

		Deploy( releases, Scripts() );

		List<string> journaled = Journal();

		RecordingListener second = new();

		Deploy( releases, Scripts(), listener: second );

		Assert.Contains( "nothing", second.Events );
		Assert.Equal( journaled, Journal() );
	}

	/// <summary>
	/// The resume path, read out of the database rather than out of a seeded fake: a plan that failed
	/// at plan commit level leaves its predecessors journaled, and the next run skips exactly those.
	/// </summary>
	public void AFailedPlanIsResumedFromWhatTheJournalNames()
	{
		IReadOnlyList<ReleaseGroup> releases = TwoReleases();

		InMemoryScripts broken = Scripts();

		broken.Deploy( "b", "s1", "insert into nowhere ( id ) values ( 1 )" );

		Assert.Throws<StepScriptFailedException>( () => Deploy( releases, broken, DeploymentCommitLevel.Plan ) );

		Assert.Equal(
			[
				"DeployingRelease|r1||",
				"DeployingPlan|r1|a|",
				"DeployingStep|r1|a|s1",
				"StepDeployed|r1|a|s1",
				"PlanDeployed|r1|a|"
			],
			Journal()
		);

		RecordingListener second = new();

		Deploy( releases, Scripts(), DeploymentCommitLevel.Plan, second );

		Assert.Contains( "skip-plan r1/a", second.Events );
		Assert.DoesNotContain( "skip-plan r1/b", second.Events );
		Assert.Equal( [ "a_s1", "b_s1", "c_s1" ], StepTables() );

		// r1's start is not journaled twice, and its completion lands once the rest of it is deployed
		Assert.Equal( [ "r1", "r2" ], Journal( "DeployingRelease" ) );
		Assert.Equal( [ "r1", "r2" ], Journal( "ReleaseDeployed" ) );
	}

	// ------------------------------------------------------------------ compensating rollback

	/// <summary>
	/// The claim that makes rollback compensating rather than transactional, against a server that
	/// could prove it wrong: at plan commit level the plan before the failure is already durable, so
	/// no database transaction could take it back — only its own rollback script can, and the engine
	/// has to run it.
	/// </summary>
	public void OnErrorUndoesWhatTheRunAlreadyCommitted()
	{
		IReadOnlyList<ReleaseGroup> releases = TwoReleases();

		InMemoryScripts broken = Scripts();

		broken.Deploy( "b", "s1", "insert into nowhere ( id ) values ( 1 )" );

		Assert.Throws<StepScriptFailedException>( () => Deploy(
				releases,
				broken,
				DeploymentCommitLevel.Plan,
				rollback: RollbackTrigger.OnError
			)
		);

		// a was committed and is now compensated for; b never reached the database — though where DDL
		// commits itself the engine cannot know that, and compensates b's failed step as well
		Assert.Empty( StepTables() );

		List<string> failedPlan = CommitsDdlImplicitly() ?
			[ "RollingBackPlan|r1|b|", "RollingBackStep|r1|b|s1", "RolledBackStep|r1|b|s1", "RolledBackPlan|r1|b|" ] :
			[ ];

		Assert.Equal(
			[
				"DeployingRelease|r1||",
				"DeployingPlan|r1|a|",
				"DeployingStep|r1|a|s1",
				"StepDeployed|r1|a|s1",
				"PlanDeployed|r1|a|",
				"RollingBackRelease|r1||",
				..failedPlan,
				"RollingBackPlan|r1|a|",
				"RollingBackStep|r1|a|s1",
				"RolledBackStep|r1|a|s1",
				"RolledBackPlan|r1|a|",
				"RolledBackRelease|r1||"
			],
			Journal()
		);
	}

	/// <summary>
	/// Where DDL commits itself, a plan failing partway leaves its earlier steps' changes — and the
	/// failing step's own — in place whatever its transaction does. On-error has to undo them all, and
	/// the journal has to show the release rolled back so the next run starts clean, as a fixed bundle
	/// then does. Only engines that commit DDL by themselves run this.
	/// </summary>
	public void OnErrorUndoesAPlanTheServerCommittedPartway()
	{
		IReadOnlyList<ReleaseGroup> releases = Lineage( [ Release( "r1" ) ], [ Plan( "p", "r1", "s1", "s2" ) ] );

		InMemoryScripts broken = Scripts();

		broken.Deploy( "p", "s2", "create table p_s2 ( id integer primary key ); insert into nowhere ( id ) values ( 1 )" );

		Assert.Throws<StepScriptFailedException>( () => Deploy(
				releases,
				broken,
				DeploymentCommitLevel.Release,
				rollback: RollbackTrigger.OnError
			)
		);

		Assert.Empty( StepTables() );

		Assert.Equal(
			[
				"DeployingRelease|r1||",
				"DeployingPlan|r1|p|",
				"DeployingStep|r1|p|s1",
				"StepDeployed|r1|p|s1",
				"DeployingStep|r1|p|s2",
				"RollingBackRelease|r1||",
				"RollingBackPlan|r1|p|",
				"RollingBackStep|r1|p|s2",
				"RolledBackStep|r1|p|s2",
				"RollingBackStep|r1|p|s1",
				"RolledBackStep|r1|p|s1",
				"RolledBackPlan|r1|p|",
				"RolledBackRelease|r1||"
			],
			Journal()
		);

		// the release reads as rolled back, so the fixed bundle deploys from the start
		Deploy( releases, Scripts() );

		Assert.Equal( [ "p_s1" ], StepTables() );
		Assert.Equal( [ "PlanDeployed|r1|p|", "ReleaseDeployed|r1||" ], Journal().TakeLast( 2 ) );
	}

	/// <summary>
	/// Proving a bundle applies cleanly and leaving the database as it was. Releases are undone in
	/// reverse, and the plans within a release with them — asserted here rather than inferred,
	/// because the order is the one thing a compensating rollback cannot get wrong.
	/// </summary>
	public void OnSuccessUndoesTheWholeDeploymentInReverse()
	{
		IReadOnlyList<ReleaseGroup> releases = TwoReleases();

		Deploy( releases, Scripts(), rollback: RollbackTrigger.OnSuccess );

		Assert.Empty( StepTables() );

		Assert.Equal(
			[
				"RollingBackRelease|r2||",
				"RollingBackPlan|r2|c|",
				"RollingBackStep|r2|c|s1",
				"RolledBackStep|r2|c|s1",
				"RolledBackPlan|r2|c|",
				"RolledBackRelease|r2||",
				"RollingBackRelease|r1||",
				"RollingBackPlan|r1|b|",
				"RollingBackStep|r1|b|s1",
				"RolledBackStep|r1|b|s1",
				"RolledBackPlan|r1|b|",
				"RollingBackPlan|r1|a|",
				"RollingBackStep|r1|a|s1",
				"RolledBackStep|r1|a|s1",
				"RolledBackPlan|r1|a|",
				"RolledBackRelease|r1||"
			],
			Journal().Where( row => row.Contains( "RollingBack" ) || row.Contains( "RolledBack" ) )
		);
	}

	/// <summary>
	/// A rollback script that fails replaces the failure that triggered it, and its transaction is
	/// rolled back rather than left half applied — so the journal shows no compensation that did not
	/// finish, and the deployed table is still there to be dealt with by hand.
	/// </summary>
	public void AFailingRollbackScriptLeavesNoHalfFinishedCompensation()
	{
		IReadOnlyList<ReleaseGroup> releases = TwoReleases();

		InMemoryScripts broken = Scripts();

		broken.Deploy( "b", "s1", "insert into nowhere ( id ) values ( 1 )" );
		broken.Rollback( "a", "s1", "drop table nowhere_at_all" );

		RollbackFailedException error = Assert.Throws<RollbackFailedException>( () => Deploy(
				releases,
				broken,
				DeploymentCommitLevel.Plan,
				rollback: RollbackTrigger.OnError
			)
		);

		Assert.Equal( ( "r1", "a", "s1" ), ( error.Release, error.Plan, error.Step ) );

		Assert.Equal( [ "a_s1" ], StepTables() );
		Assert.Empty( Journal().Where( row => row.Contains( "RollingBack" ) || row.Contains( "RolledBack" ) ) );
	}

	// ------------------------------------------------------------------ standalone rollback

	/// <summary>
	/// Undoing a deployment that finished, to a release: what the journal says is deployed decides what
	/// is undone, and the template's own queries must read the result back as the earlier release,
	/// completed — so the next deployment carries on from there, as if the later release never ran.
	/// </summary>
	public void RollingBackToAReleaseUndoesTheLaterOnesAndTheyCanBeDeployedAgain()
	{
		IReadOnlyList<ReleaseGroup> releases = TwoReleases();

		Deploy( releases, Scripts() );
		RollBack( releases, Scripts(), RollbackTarget.ToRelease( "r1" ) );

		Assert.Equal( [ "a_s1", "b_s1" ], StepTables() );
		Assert.Equal( new JournaledRelease( "r1", Completed: true ), JournalLatest() );
		Assert.Empty( JournalPlans( "r2" ) );
		Assert.Equal( [ "a", "b" ], JournalPlans( "r1" ) );

		RecordingListener again = new();

		Deploy( releases, Scripts(), listener: again );

		Assert.Contains( "release r2", again.Events );
		Assert.Contains( "plan r2/c", again.Events );
		Assert.DoesNotContain( "release r1", again.Events );
		Assert.Equal( [ "a_s1", "b_s1", "c_s1" ], StepTables() );
		Assert.Equal( new JournaledRelease( "r2", Completed: true ), JournalLatest() );
		Assert.Equal( [ "c" ], JournalPlans( "r2" ) );
	}

	/// <summary>
	/// Undoing one plan of a release leaves the release deployed only in part: the journal reads it as
	/// not completed with the other plan still held, and deploying again resumes inside it, deploying
	/// just the plan that was undone.
	/// </summary>
	public void RollingBackAPlanLeavesTheReleaseIncompleteAndRedeployingDeploysOnlyThatPlan()
	{
		IReadOnlyList<ReleaseGroup> releases = [
			new ReleaseGroup(
				"r1",
				"",
				"",
				[
					new DeployablePlan( "a", "", [ new DeployableStep( "s1", "" ) ] ),
					new DeployablePlan( "b", "", [ new DeployableStep( "s1", "" ) ], [ "a" ] )
				]
			)
		];

		Deploy( releases, Scripts() );
		RollBack( releases, Scripts(), RollbackTarget.OfPlans( [ "b" ] ) );

		Assert.Equal( [ "a_s1" ], StepTables() );
		Assert.Equal( new JournaledRelease( "r1", Completed: false ), JournalLatest() );
		Assert.Equal( [ "a" ], JournalPlans( "r1" ) );

		RecordingListener again = new();

		Deploy( releases, Scripts(), listener: again );

		Assert.Contains( "skip-plan r1/a", again.Events );
		Assert.Contains( "plan r1/b", again.Events );
		Assert.DoesNotContain( "plan r1/a", again.Events );
		Assert.Equal( [ "a_s1", "b_s1" ], StepTables() );
		Assert.Equal( new JournaledRelease( "r1", Completed: true ), JournalLatest() );
		Assert.Equal( [ "a", "b" ], JournalPlans( "r1" ) );
	}

	/// <summary>
	/// Undoing everything: the journal then reports nothing deployed at all, which is what a database
	/// never deployed to reports, so a redeploy starts from the first release.
	/// </summary>
	public void RollingBackEverythingLeavesAJournalThatReportsNothingDeployedAndRedeployingDeploysAll()
	{
		IReadOnlyList<ReleaseGroup> releases = TwoReleases();

		Deploy( releases, Scripts() );
		RollBack( releases, Scripts(), RollbackTarget.All );

		Assert.Empty( StepTables() );
		Assert.Null( JournalLatest() );

		RecordingListener again = new();

		Deploy( releases, Scripts(), listener: again );

		Assert.Equal(
			[ "plan r1/a", "plan r1/b", "plan r2/c" ],
			again.Events.Where( e => e.StartsWith( "plan " ) )
		);
		Assert.Equal( [ "a_s1", "b_s1", "c_s1" ], StepTables() );
		Assert.Equal( new JournaledRelease( "r2", Completed: true ), JournalLatest() );
	}

	/// <summary>
	/// Rolling back a database never deployed to is refused by the journal read failing, and the
	/// rollback, unlike a deployment, does not create the journal to be able to read it.
	/// </summary>
	public void RollingBackAFreshDatabaseFailsTheJournalReadAndDoesNotCreateTheJournal()
	{
		Assert.False( JournalTableExists() );

		JournalingStatementException error = Assert.Throws<JournalingStatementException>(
			() => RollBack( TwoReleases(), Scripts(), RollbackTarget.All )
		);

		Assert.Equal( nameof(JournalingSlot.GetLatestRelease), error.Slot );
		Assert.False( JournalTableExists() );
		Assert.Empty( StepTables() );
	}

	/// <summary>
	/// A rollback script that fails stops the rollback with what committed before it standing, and the
	/// journal says exactly that, so running the same rollback again finishes it.
	/// </summary>
	public void AFailingRollbackScriptStopsAStandaloneRollbackAndRunningItAgainFinishesIt()
	{
		IReadOnlyList<ReleaseGroup> releases = TwoReleases();

		Deploy( releases, Scripts() );

		InMemoryScripts broken = Scripts();

		broken.Rollback( "b", "s1", "drop table nowhere_at_all" );

		RollbackFailedException error = Assert.Throws<RollbackFailedException>( () => RollBack(
				releases,
				broken,
				RollbackTarget.All
			)
		);

		Assert.Equal( ( "r1", "b", "s1" ), ( error.Release, error.Plan, error.Step ) );

		// r2 was undone and committed before r1 began; nothing of r1 was. Where the failing DROP commits
		// implicitly the journal rows before it stand, 'RollingBackRelease r1' among them, so r1 reads as
		// begun to be undone, which is not completed
		Assert.Equal( [ "a_s1", "b_s1" ], StepTables() );
		Assert.Equal( new JournaledRelease( "r1", Completed: !CommitsDdlImplicitly() ), JournalLatest() );
		Assert.Empty( JournalPlans( "r2" ) );
		Assert.Equal( [ "a", "b" ], JournalPlans( "r1" ) );

		RollBack( releases, Scripts(), RollbackTarget.All );

		Assert.Empty( StepTables() );
		Assert.Null( JournalLatest() );
	}

	/// <summary>
	/// A plan-level rollback cut short inside a release holding two plans: the second plan's rollback
	/// script fails after the first plan was undone and committed, together with the release's
	/// <c>RollingBackRelease</c> but not its <c>RolledBackRelease</c>. The template must read that release
	/// as not completed — its <c>ReleaseDeployed</c> predates the rollback — so a deployment resumes into
	/// it and redeploys the undone plan, and the rollback rerun then finishes.
	/// </summary>
	public void ARollbackCutShortInsideAReleaseLeavesItIncompleteAndRedeployingRestoresWhatWasUndone()
	{
		IReadOnlyList<ReleaseGroup> releases = Lineage(
			[ Release( "r1" ), Release( "r2", "r1" ) ],
			[ Plan( "a", "r1", "s1" ), Plan( "b", "r2", "s1" ), Plan( "c", "r2", "s1" ) ]
		);

		Deploy( releases, Scripts() );

		Assert.Equal( [ "a_s1", "b_s1", "c_s1" ], StepTables() );

		InMemoryScripts broken = Scripts();

		broken.Rollback( "b", "s1", "drop table nowhere_at_all" );

		// plans are undone newest first: c, then b, whose script fails
		RollbackFailedException error = Assert.Throws<RollbackFailedException>( () => RollBack(
				releases,
				broken,
				RollbackTarget.ToRelease( "r1" ),
				DeploymentCommitLevel.Plan
			)
		);

		Assert.Equal( ( "r2", "b", "s1" ), ( error.Release, error.Plan, error.Step ) );
		Assert.Equal( [ "a_s1", "b_s1" ], StepTables() );
		Assert.Equal( new JournaledRelease( "r2", Completed: false ), JournalLatest() );
		Assert.Equal( [ "b" ], JournalPlans( "r2" ) );

		RecordingListener again = new();

		Deploy( releases, Scripts(), listener: again );

		Assert.Contains( "skip-plan r2/b", again.Events );
		Assert.Contains( "plan r2/c", again.Events );
		Assert.DoesNotContain( "plan r2/b", again.Events );
		Assert.Equal( [ "a_s1", "b_s1", "c_s1" ], StepTables() );
		Assert.Equal( new JournaledRelease( "r2", Completed: true ), JournalLatest() );
		Assert.Equal( [ "b", "c" ], JournalPlans( "r2" ) );

		RollBack( releases, Scripts(), RollbackTarget.ToRelease( "r1" ), DeploymentCommitLevel.Plan );

		Assert.Equal( [ "a_s1" ], StepTables() );
		Assert.Equal( new JournaledRelease( "r1", Completed: true ), JournalLatest() );
		Assert.Empty( JournalPlans( "r2" ) );
	}

	// ------------------------------------------------------------------ step verification

	/// <summary>
	/// A verify script is run for its errors alone, so the one thing worth proving on a real provider
	/// is that a <c>select</c> run as a command surfaces a <em>run-time</em> error — not merely a
	/// compile-time one — and that the failure takes the step back with it. The deploy creates the
	/// table; the verify then fails at run time (each provider supplies its own SQL, since which
	/// expressions raise at run time differs by engine). Where the transaction holds DDL nothing
	/// reaches the database; where DDL commits itself, on-error compensation removes the table.
	/// </summary>
	/// <param name="failingVerify">SQL that fails at run time; it may or may not read the new table 'v_s1'.</param>
	public void AFailingVerifyFailsTheStep( string failingVerify )
	{
		IReadOnlyList<ReleaseGroup> releases = Lineage( [ Release( "r1" ) ], [ Plan( "v", "r1", "s1" ) ] );

		InMemoryScripts scripts = Scripts();

		scripts.Verify( "v", "s1", failingVerify );

		StepVerificationFailedException error = Assert.Throws<StepVerificationFailedException>( () => Deploy(
				releases,
				scripts,
				rollback: CommitsDdlImplicitly() ? RollbackTrigger.OnError : RollbackTrigger.None
			)
		);

		Assert.Equal( ( "r1", "v", "s1" ), ( error.Release, error.Plan, error.Step ) );
		Assert.NotNull( error.InnerException );

		Assert.Empty( StepTables() );
		Assert.DoesNotContain( "StepDeployed|r1|v|s1", Journal() );
	}

	// ------------------------------------------------------------------ status (read-only forecast)

	/// <summary>
	/// A database nothing was ever deployed to has no journal table. Status must say so through the
	/// template's own JournalExists query — and, being read-only, must not create the table by asking.
	/// </summary>
	public void StatusOfAFreshDatabaseSaysSoAndDoesNotCreateTheJournal()
	{
		Assert.False( JournalTableExists() );

		DeploymentStatus status = Status( TwoReleases() );

		Assert.False( status.JournalExists );
		Assert.Null( status.Forecast.Journaled );
		Assert.Empty( status.Forecast.Notes );
		Assert.Equal( [ "r1/a", "r1/b", "r2/c" ], Planned( status ) );

		Assert.False( JournalTableExists() );
		Assert.Empty( StepTables() );
	}

	/// <summary>
	/// After 'PrepareJournal' alone the journal exists and holds nothing: the database is empty
	/// rather than fresh, and everything would still deploy.
	/// </summary>
	public void StatusOfAnEmptyJournalDeploysEverything()
	{
		PrepareJournalOnly();

		Assert.True( JournalTableExists() );

		DeploymentStatus status = Status( TwoReleases() );

		Assert.True( status.JournalExists );
		Assert.Null( status.Forecast.Journaled );
		Assert.Equal( [ "r1/a", "r1/b", "r2/c" ], Planned( status ) );
		Assert.Equal( [ "0" ], query( "select count(*) from sqetch_journal" ) );
		Assert.Empty( StepTables() );
	}

	/// <summary>
	/// The resume branch, read out of a journal a failed run wrote: the deployed plan is skipped, the
	/// rest listed — and a deploy that then runs does exactly what status said it would.
	/// </summary>
	public void StatusOfAPartlyDeployedReleaseListsTheRemainingPlansAndDeployThenDoesExactlyThat()
	{
		IReadOnlyList<ReleaseGroup> releases = TwoReleases();

		InMemoryScripts broken = Scripts();

		broken.Deploy( "b", "s1", "insert into nowhere ( id ) values ( 1 )" );

		Assert.Throws<StepScriptFailedException>( () => Deploy( releases, broken, DeploymentCommitLevel.Plan ) );

		List<string> journalBefore = Journal();

		DeploymentStatus status = Status( releases );

		Assert.True( status.JournalExists );
		Assert.Equal( new JournaledRelease( "r1", Completed: false ), status.Forecast.Journaled );
		Assert.Equal( [ "a" ], status.Forecast.Releases[0].SkippedPlans );
		Assert.True( status.Forecast.Releases[0].Resumed );
		Assert.Equal( [ "r1/b", "r2/c" ], Planned( status ) );

		// read-only: the journal and the schema are as the failed run left them
		Assert.Equal( journalBefore, Journal() );
		Assert.Equal( [ "a_s1" ], StepTables() );

		RecordingListener listener = new();

		Deploy( releases, Scripts(), DeploymentCommitLevel.Plan, listener );

		Assert.Equal(
			Planned( status ).Select( plan => $"plan {plan}" ),
			listener.Events.Where( e => e.StartsWith( "plan " ) ) );
	}

	/// <summary>
	/// The hand-over branch: a database whose release completed carries on with the bundle's next one,
	/// and a bundle that holds only the releases after it is a continuation from where the journal is.
	/// </summary>
	public void StatusOfACompletedReleaseHandsOverAndAContinuationBundleSaysWhereItContinuesFrom()
	{
		Deploy( TwoReleases(), Scripts() );

		IReadOnlyList<ReleaseGroup> three = Lineage(
			[ Release( "r1" ), Release( "r2", "r1" ), Release( "r3", "r2" ) ],
			[ Plan( "a", "r1", "s1" ), Plan( "b", "r1", "s1" ), Plan( "c", "r2", "s1" ), Plan( "d", "r3", "s1" ) ] );

		List<string> journalBefore = Journal();

		DeploymentStatus whole = Status( three );

		Assert.Equal( new JournaledRelease( "r2", Completed: true ), whole.Forecast.Journaled );
		Assert.Null( whole.Forecast.ContinuingFrom );
		Assert.Equal( [ "r3/d" ], Planned( whole ) );

		DeploymentStatus continuation = Status( three.Skip( 2 ).ToList() );

		Assert.Equal( "r2", continuation.Forecast.ContinuingFrom );
		Assert.Equal( [ "r3/d" ], Planned( continuation ) );

		Assert.Equal( journalBefore, Journal() );
		Assert.Equal( [ "a_s1", "b_s1", "c_s1" ], StepTables() );

		RecordingListener listener = new();

		Deploy( three.Skip( 2 ).ToList(), Scripts(), listener: listener );

		Assert.Contains( "continuing r2", listener.Events );
		Assert.Contains( "plan r3/d", listener.Events );
	}

	/// <summary>The last branch: the journal is at the bundle's last release, so nothing would deploy.</summary>
	public void StatusOfAnUpToDateDatabaseHasNothingToDeploy()
	{
		Deploy( TwoReleases(), Scripts() );

		List<string> journalBefore = Journal();

		DeploymentStatus status = Status( TwoReleases() );

		Assert.True( status.Forecast.NothingToDeploy );
		Assert.Equal( new JournaledRelease( "r2", Completed: true ), status.Forecast.Journaled );
		Assert.Empty( Planned( status ) );
		Assert.Equal( journalBefore, Journal() );
	}

	/// <summary>
	/// A bundle that does not fit is refused as deploying it would be, and the refusal writes nothing
	/// either.
	/// </summary>
	public void StatusRefusesAContinuationBundleOnAFreshDatabaseAndWritesNothing()
	{
		IReadOnlyList<ReleaseGroup> continuation = TwoReleases().Skip( 1 ).ToList();

		Assert.Throws<IncompatibleBundleException>( () => Status( continuation ) );

		Assert.False( JournalTableExists() );
	}

	// ------------------------------------------------------------------ the deployment lock

	/// <summary>
	/// Another session already holds the lock, as a deployment running elsewhere would: the deployment
	/// is refused before it provisions anything, and goes through once the holder's connection is gone.
	/// </summary>
	public void ALockHeldElsewhereRefusesTheDeploymentAndCreatesNothing()
	{
		IReadOnlyList<ReleaseGroup> releases = TwoReleases();

		using( IDatabaseDriver holder = driver() )
		{
			Assert.True( TakeLock( holder ), "the first session should have taken the lock" );

			RecordingListener refused = new();

			Assert.Throws<DeploymentLockedException>( () => Deploy( releases, Scripts(), listener: refused ) );

			Assert.DoesNotContain( "lock-acquired", refused.Events );
			Assert.Empty( StepTables() );

			// refused before the journal's provisioning, which two deployments could otherwise race on
			Assert.False( JournalTableExists() );
		}

		endPooledSessions?.Invoke();

		Deploy( releases, Scripts() );

		Assert.Equal( [ "a_s1", "b_s1", "c_s1" ], StepTables() );
	}

	/// <summary>
	/// The deploying connection stays open, so it is the ReleaseLock statement and not the connection
	/// closing that gives the lock back.
	/// </summary>
	public void TheLockIsReleasedWhileTheDeployingConnectionIsStillOpen()
	{
		IReadOnlyList<ReleaseGroup> releases = TwoReleases();
		RecordingListener listener = new();

		using IDatabaseDriver deploying = driver();

		new DeploymentEngine(
				deploying,
				journaling,
				Scripts(),
				DeploymentCommitLevel.Release,
				listener,
				RollbackTrigger.None
			)
			.Deploy( Project, releases );

		Assert.Contains( "lock-acquired", listener.Events );
		Assert.DoesNotContain( listener.Events, x => x.StartsWith( "lock-not-released" ) );

		using IDatabaseDriver other = driver();

		Assert.True( TakeLock( other ), "the deployment should have given the lock back" );
	}

	/// <summary>
	/// The issue's 'done when', made deterministic: the first deployment's step blocks on a lock the test
	/// holds, so it is running for exactly as long as the test wants. A second one started meanwhile is
	/// refused, and once the first is let go and done the second has nothing left to do.
	/// </summary>
	/// <param name="takeBlocker">Takes the session lock the first deployment's step will wait on.</param>
	/// <param name="releaseBlocker">Gives that lock back, letting the step finish.</param>
	/// <param name="blockedStep">A deploy script for plan 'a', step 's1': creates its table, then waits on the blocker.</param>
	/// <param name="lockHeldQuery">Selects one count, nonzero while some session holds the project's deployment lock.</param>
	public void ASecondDeploymentIsRefusedWhileTheFirstIsRunning(
		string takeBlocker,
		string releaseBlocker,
		string blockedStep,
		string lockHeldQuery
	)
	{
		IReadOnlyList<ReleaseGroup> releases = TwoReleases();

		using IDatabaseDriver blocker = driver();

		Run( blocker, takeBlocker );

		bool released = false;
		Task? first = null;

		try
		{
			first = Task.Run( () => Deploy( releases, Scripts().Deploy( "a", "s1", blockedStep ) ) );

			DateTime giveUp = DateTime.UtcNow.AddSeconds( 30 );

			while( query( lockHeldQuery )[0] == "0" )
			{
				Assert.False( first.IsCompleted, "the first deployment ended before its lock was seen held" );
				Assert.True( DateTime.UtcNow < giveUp, "the first deployment never took the lock" );

				Thread.Sleep( 25 );
			}

			Assert.False( first.IsCompleted, "the first deployment should still be running" );

			Assert.Throws<DeploymentLockedException>( () => Deploy( releases, Scripts() ) );

			Run( blocker, releaseBlocker );
			released = true;
		}
		finally
		{
			if( !released )
				Run( blocker, releaseBlocker );

			// surfaces the first deployment's own failure, if it had one
			if( first is not null )
				Assert.True( first.Wait( TimeSpan.FromSeconds( 60 ) ), "the first deployment never finished" );
		}

		Assert.Equal( [ "a_s1", "b_s1", "c_s1" ], StepTables() );

		RecordingListener retry = new();

		Deploy( releases, Scripts(), listener: retry );

		Assert.Contains( "nothing", retry.Events );
	}

	/// <summary>Runs one script in a committed transaction of its own on a driver the caller keeps open.</summary>
	private static void Run( IDatabaseDriver on, string script )
	{
		IDatabaseTransaction transaction = on.BeginTransaction();

		transaction.ExecuteStepScript( script );
		transaction.Commit();
	}

	/// <summary>
	/// A refusal has to be a clear 'no' from the template's AcquireLock — false, never NULL — since a
	/// NULL is a journaling failure (exit 42) rather than the busy database (exit 34).
	/// </summary>
	public void AcquireLockAnswersFalseWhileTheLockIsHeldElsewhere()
	{
		using IDatabaseDriver holder = driver();
		using IDatabaseDriver other = driver();

		Assert.True( TakeLock( holder ) );
		Assert.False( TakeLock( other ) );
	}

	/// <summary>Takes the lock as a deployment does — AcquireLock, committed — and keeps the connection.</summary>
	private bool TakeLock( IDatabaseDriver holder )
	{
		IDatabaseTransaction transaction = holder.BeginTransaction();

		bool? acquired = new StatementJournal( journaling, Project ).AcquireLock( transaction );

		transaction.Commit();

		Assert.NotNull( acquired );

		return acquired.Value;
	}

	private DeploymentStatus Status( IReadOnlyList<ReleaseGroup> releases )
	{
		using IDatabaseDriver owned = driver();

		return new DeploymentForecaster( owned, journaling ).Forecast( Project, releases );
	}

	private static List<string> Planned( DeploymentStatus status )
		=> status.Forecast.Releases
			.SelectMany( release => release.PlansToDeploy.Select( plan => $"{release.Group.Name}/{plan.Name}" ) )
			.ToList();

	/// <summary>Creates the journal and journals nothing, as 'PrepareJournal' alone does.</summary>
	private void PrepareJournalOnly()
	{
		using IDatabaseDriver owned = driver();

		IDatabaseTransaction transaction = owned.BeginTransaction();

		new StatementJournal( journaling, Project ).PrepareJournal( transaction );

		transaction.Commit();
	}

	/// <summary>
	/// Whether the journal table exists, asked of the template's own JournalExists query so that the
	/// query itself is what the scenarios prove right on this provider.
	/// </summary>
	private bool JournalTableExists()
		=> query( journaling.Text( JournalingSlot.JournalExists ) ).FirstOrDefault()?.Split( '|' )[0].ToLowerInvariant()
			is "1" or "true";

	// ------------------------------------------------------------------ helpers

	private void Deploy(
		IReadOnlyList<ReleaseGroup> releases,
		IScriptContentReader scripts,
		DeploymentCommitLevel level = DeploymentCommitLevel.Release,
		RecordingListener? listener = null,
		RollbackTrigger rollback = RollbackTrigger.None
	)
	{
		using IDatabaseDriver owned = driver();

		new DeploymentEngine(
				owned,
				journaling,
				scripts,
				level,
				listener ?? new RecordingListener(),
				rollback
			)
			.Deploy( Project, releases );
	}

	private void RollBack(
		IReadOnlyList<ReleaseGroup> releases,
		IScriptContentReader scripts,
		RollbackTarget target,
		DeploymentCommitLevel level = DeploymentCommitLevel.Release
	)
	{
		using IDatabaseDriver owned = driver();

		new DeploymentEngine( owned, journaling, scripts, level, new RecordingListener() )
			.RollBack( Project, releases, target );
	}

	/// <summary>What the template's GetLatestRelease answers now, read as a deployment would.</summary>
	private JournaledRelease? JournalLatest()
	{
		using IDatabaseDriver owned = driver();

		IDatabaseTransaction transaction = owned.BeginTransaction();

		JournaledRelease? latest = new StatementJournal( journaling, Project ).GetLatestRelease( transaction );

		transaction.Commit();

		return latest;
	}

	/// <summary>What the template's GetDeployedPlans answers now for a release, in name order.</summary>
	private List<string> JournalPlans( string release )
	{
		using IDatabaseDriver owned = driver();

		IDatabaseTransaction transaction = owned.BeginTransaction();

		List<string> plans = new StatementJournal( journaling, Project ).GetDeployedPlans( release, transaction )
			.OrderBy( plan => plan, StringComparer.Ordinal )
			.ToList();

		transaction.Commit();

		return plans;
	}

	/// <summary>
	/// A deploy script per step, each creating a table named after the plan and step, a verify script
	/// selecting from it, so every scenario runs the passing verification path, and the rollback script
	/// that undoes it — which is what compensation runs, so it has to be real SQL here rather than a
	/// marker.
	/// </summary>
	private static InMemoryScripts Scripts()
	{
		InMemoryScripts reader = new(
			( plan, step ) => $"create table {plan}_{step} ( id integer primary key )",
			// 'if exists': where DDL commits itself, compensation also runs for a step that never applied
			( plan, step ) => $"drop table if exists {plan}_{step}",
			// reads what the step's deploy created, so a verify that cannot see it fails the step
			( plan, step ) => $"select id from {plan}_{step} where 1 = 0"
		);

		return reader;
	}

	private List<string> StepTables() => stepTables();

	private bool CommitsDdlImplicitly()
	{
		using IDatabaseDriver probe = driver();

		return probe.CommitsDdlImplicitly;
	}

	/// <summary>The journal as 'slot|release|plan|step' rows, in the order they were written.</summary>
	private List<string> Journal()
		=> query(
			$"select slot, {Column( "release" )}, coalesce( {Column( "plan" )}, '' ), coalesce( step, '' ) from sqetch_journal order by id"
		);

	private List<string> Journal( string slot )
		=> query( $"select {Column( "release" )} from sqetch_journal where slot = '{slot}' order by id" );
}