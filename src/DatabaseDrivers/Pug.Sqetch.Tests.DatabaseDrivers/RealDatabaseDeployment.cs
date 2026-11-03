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
public sealed class RealDatabaseDeployment(
	Func<IDatabaseDriver> driver,
	JournalingStatements journaling,
	Func<string, List<string>> query,
	Func<List<string>> stepTables,
	Func<string, string>? identifier = null
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