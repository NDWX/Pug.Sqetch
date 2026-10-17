using System.Globalization;
using Pug.Sqetch.Bundling;
using Pug.Sqetch.Bundling.Layouts;
using Pug.Sqetch.Deployment;
using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Models;
using Pug.Sqetch.Tests.Deployment;
using static Pug.Sqetch.Tests.Deployment.Manifests;

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
public sealed class RealDatabaseDeployment(
	Func<IDatabaseDriver> driver, JournalingStatements journaling, Func<string, List<string>> query,
	Func<List<string>> stepTables )
{
	private readonly DefaultBundleLayout _layout = new ();

	/// <summary>Two releases, three plans, one step each; every step creates its own table.</summary>
	private static BundleManifest TwoReleases()
		=> Manifest(
			[Release( "r1" ), Release( "r2", "r1" )],
			[Plan( "a", "r1", "s1" ), Plan( "b", "r1", "s1" ), Plan( "c", "r2", "s1" )] );

	public void AWholeBundleIsAppliedAndJournaled()
	{
		BundleManifest manifest = TwoReleases();

		Deploy( manifest, Scripts( manifest ) );

		Assert.Equal( ["a_s1", "b_s1", "c_s1"], StepTables() );

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
			Journal() );
	}

	/// <summary>
	/// The parameter exists for the engine that cannot supply the instant itself, so what matters is
	/// that the host's clock is what reached the row — not merely that the column is populated.
	/// </summary>
	public void TheHostsUtcInstantIsWhatTheJournalRecords()
	{
		DateTime before = DateTime.UtcNow.AddSeconds( -5 );

		BundleManifest manifest = TwoReleases();

		Deploy( manifest, Scripts( manifest ) );

		List<DateTime> instants = query( "select at_utc from sqetch_journal order by id" )
									.Select(
										at => DateTime.Parse(
											at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal ) )
									.ToList();

		Assert.Equal( 16, instants.Count );
		Assert.All( instants, instant => Assert.InRange( instant, before, DateTime.UtcNow.AddSeconds( 5 ) ) );

		// the clock is read per event, so a release's start cannot be stamped after its completion
		Assert.Equal( instants.OrderBy( instant => instant ), instants );
	}

	public void RedeployingTheSameBundleDeploysNothing()
	{
		BundleManifest manifest = TwoReleases();

		Deploy( manifest, Scripts( manifest ) );

		List<string> journaled = Journal();

		RecordingListener second = new ();

		Deploy( manifest, Scripts( manifest ), listener: second );

		Assert.Contains( "nothing", second.Events );
		Assert.Equal( journaled, Journal() );
	}

	/// <summary>
	/// The resume path, read out of the database rather than out of a seeded fake: a plan that failed
	/// at plan commit level leaves its predecessors journaled, and the next run skips exactly those.
	/// </summary>
	public void AFailedPlanIsResumedFromWhatTheJournalNames()
	{
		BundleManifest manifest = TwoReleases();

		InMemoryBundleReader broken = Scripts( manifest );

		broken.Add( _layout.ScriptPath( "b", "s1", StepScriptKind.Deploy ), "insert into nowhere ( id ) values ( 1 )" );

		Assert.Throws<StepScriptFailedException>( () => Deploy( manifest, broken, DeploymentCommitLevel.Plan ) );

		Assert.Equal(
			[
				"DeployingRelease|r1||", "DeployingPlan|r1|a|", "DeployingStep|r1|a|s1", "StepDeployed|r1|a|s1",
				"PlanDeployed|r1|a|"
			],
			Journal() );

		RecordingListener second = new ();

		Deploy( manifest, Scripts( manifest ), DeploymentCommitLevel.Plan, second );

		Assert.Contains( "skip-plan r1/a", second.Events );
		Assert.DoesNotContain( "skip-plan r1/b", second.Events );
		Assert.Equal( ["a_s1", "b_s1", "c_s1"], StepTables() );

		// r1's start is not journaled twice, and its completion lands once the rest of it is deployed
		Assert.Equal( ["r1", "r2"], Journal( "DeployingRelease" ) );
		Assert.Equal( ["r1", "r2"], Journal( "ReleaseDeployed" ) );
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
		BundleManifest manifest = TwoReleases();

		InMemoryBundleReader broken = Scripts( manifest );

		broken.Add( _layout.ScriptPath( "b", "s1", StepScriptKind.Deploy ), "insert into nowhere ( id ) values ( 1 )" );

		Assert.Throws<StepScriptFailedException>(
			() => Deploy( manifest, broken, DeploymentCommitLevel.Plan, rollback: RollbackTrigger.OnError ) );

		// a was committed and is now compensated for; b never reached the database
		Assert.Empty( StepTables() );

		Assert.Equal(
			[
				"DeployingRelease|r1||", "DeployingPlan|r1|a|", "DeployingStep|r1|a|s1", "StepDeployed|r1|a|s1",
				"PlanDeployed|r1|a|",
				"RollingBackRelease|r1||", "RollingBackPlan|r1|a|", "RollingBackStep|r1|a|s1",
				"RolledBackStep|r1|a|s1", "RolledBackPlan|r1|a|", "RolledBackRelease|r1||"
			],
			Journal() );
	}

	/// <summary>
	/// Proving a bundle applies cleanly and leaving the database as it was. Releases are undone in
	/// reverse, and the plans within a release with them — asserted here rather than inferred,
	/// because the order is the one thing a compensating rollback cannot get wrong.
	/// </summary>
	public void OnSuccessUndoesTheWholeDeploymentInReverse()
	{
		BundleManifest manifest = TwoReleases();

		Deploy( manifest, Scripts( manifest ), rollback: RollbackTrigger.OnSuccess );

		Assert.Empty( StepTables() );

		Assert.Equal(
			[
				"RollingBackRelease|r2||", "RollingBackPlan|r2|c|", "RollingBackStep|r2|c|s1",
				"RolledBackStep|r2|c|s1", "RolledBackPlan|r2|c|", "RolledBackRelease|r2||",
				"RollingBackRelease|r1||",
				"RollingBackPlan|r1|b|", "RollingBackStep|r1|b|s1", "RolledBackStep|r1|b|s1", "RolledBackPlan|r1|b|",
				"RollingBackPlan|r1|a|", "RollingBackStep|r1|a|s1", "RolledBackStep|r1|a|s1", "RolledBackPlan|r1|a|",
				"RolledBackRelease|r1||"
			],
			Journal().Where( row => row.Contains( "RollingBack" ) || row.Contains( "RolledBack" ) ) );
	}

	/// <summary>
	/// A rollback script that fails replaces the failure that triggered it, and its transaction is
	/// rolled back rather than left half applied — so the journal shows no compensation that did not
	/// finish, and the deployed table is still there to be dealt with by hand.
	/// </summary>
	public void AFailingRollbackScriptLeavesNoHalfFinishedCompensation()
	{
		BundleManifest manifest = TwoReleases();

		InMemoryBundleReader broken = Scripts( manifest );

		broken.Add( _layout.ScriptPath( "b", "s1", StepScriptKind.Deploy ), "insert into nowhere ( id ) values ( 1 )" );
		broken.Add( _layout.ScriptPath( "a", "s1", StepScriptKind.Rollback ), "drop table nowhere_at_all" );

		RollbackFailedException error = Assert.Throws<RollbackFailedException>(
			() => Deploy( manifest, broken, DeploymentCommitLevel.Plan, rollback: RollbackTrigger.OnError ) );

		Assert.Equal( ( "r1", "a", "s1" ), (error.Release, error.Plan, error.Step) );

		Assert.Equal( ["a_s1"], StepTables() );
		Assert.Empty( Journal().Where( row => row.Contains( "RollingBack" ) || row.Contains( "RolledBack" ) ) );
	}

	// ------------------------------------------------------------------ helpers

	private void Deploy(
		BundleManifest manifest, IBundleReader reader,
		DeploymentCommitLevel level = DeploymentCommitLevel.Release, RecordingListener? listener = null,
		RollbackTrigger rollback = RollbackTrigger.None )
	{
		using IDatabaseDriver owned = driver();

		new DeploymentEngine(
				owned, journaling, reader, _layout, level, listener ?? new RecordingListener(), rollback )
			.Deploy( manifest );
	}

	/// <summary>
	/// A deploy script per step, each creating a table named after the plan and step, and the
	/// rollback script that undoes it — which is what compensation runs, so it has to be real SQL
	/// here rather than a marker.
	/// </summary>
	private InMemoryBundleReader Scripts( BundleManifest manifest )
	{
		InMemoryBundleReader reader = new ();

		foreach( BundleManifestPlan plan in manifest.Plans )
			foreach( BundleManifestStep step in plan.Steps )
			{
				reader.Add(
					_layout.ScriptPath( plan.Name, step.Name, StepScriptKind.Deploy ),
					$"create table {plan.Name}_{step.Name} ( id integer primary key )" );

				reader.Add(
					_layout.ScriptPath( plan.Name, step.Name, StepScriptKind.Rollback ),
					$"drop table {plan.Name}_{step.Name}" );
			}

		return reader;
	}

	private List<string> StepTables() => stepTables();

	/// <summary>The journal as 'slot|release|plan|step' rows, in the order they were written.</summary>
	private List<string> Journal()
		=> query(
			"select slot, release, coalesce( plan, '' ), coalesce( step, '' ) from sqetch_journal order by id" );

	private List<string> Journal( string slot )
		=> query( $"select release from sqetch_journal where slot = '{slot}' order by id" );
}
