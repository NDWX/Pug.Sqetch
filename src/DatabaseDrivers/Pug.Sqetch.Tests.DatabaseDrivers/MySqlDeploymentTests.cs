using MySqlConnector;

using Pug.Sqetch.DatabaseDrivers.MySql;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// <see cref="RealDatabaseDeployment"/> against MySQL and MariaDB on the 'mysql' driver's built-in
/// template, with 'release' quoted, being reserved there. Both commit DDL implicitly, which every
/// scenario but one tolerates: <c>AFailingRollbackScriptLeavesNoHalfFinishedCompensation</c> relies on
/// the compensation transaction taking its journal rows back with it, and the rollback scripts'
/// DROP TABLE has already committed them here — the half-finished compensation it proves impossible
/// elsewhere is exactly what these engines can leave, as the driver documents.
/// </summary>
public abstract class MySqlFamilyDeploymentTests
{
	private readonly Lazy<RealDatabaseDeployment> _deployment;

	protected MySqlFamilyDeploymentTests( MySqlFamilyServer server )
		// lazy, so a run without Docker skips rather than failing to create a database in the
		// constructor, where a skip is no longer available
		=> _deployment = new Lazy<RealDatabaseDeployment>( () => {
				string connection = server.CreateDatabase( out _ );

				return new RealDatabaseDeployment(
					() => MySqlFamilyServer.Driver( connection ),
					BuiltInJournal.Statements( new MySqlDatabaseDriverFactory() ),
					sql => MySqlFamilyServer.Query( connection, sql ),
					() => MySqlFamilyServer.Query(
						connection,
						"""
						select table_name from information_schema.tables
						where table_schema = database() and table_name like '%\_s1'
						order by table_name
						"""
					),
					name => $"`{name}`",
					endPooledSessions: MySqlConnection.ClearAllPools
				);
			}
		);

	[DockerFact]
	public void AWholeBundleIsAppliedAndJournaled() => _deployment.Value.AWholeBundleIsAppliedAndJournaled();

	[DockerFact]
	public void TheHostsUtcInstantIsWhatTheJournalRecords()
		=> _deployment.Value.TheHostsUtcInstantIsWhatTheJournalRecords();

	[DockerFact]
	public void RedeployingTheSameBundleDeploysNothing() => _deployment.Value.RedeployingTheSameBundleDeploysNothing();

	[DockerFact]
	public void AFailedPlanIsResumedFromWhatTheJournalNames()
		=> _deployment.Value.AFailedPlanIsResumedFromWhatTheJournalNames();

	[DockerFact]
	public void OnErrorUndoesWhatTheRunAlreadyCommitted() => _deployment.Value.OnErrorUndoesWhatTheRunAlreadyCommitted();

	[DockerFact]
	public void OnSuccessUndoesTheWholeDeploymentInReverse()
		=> _deployment.Value.OnSuccessUndoesTheWholeDeploymentInReverse();

	[DockerFact]
	public void OnErrorUndoesAPlanTheServerCommittedPartway()
		=> _deployment.Value.OnErrorUndoesAPlanTheServerCommittedPartway();

	[DockerFact]
	public void AFailingVerifyFailsTheStep()
		=> _deployment.Value.AFailingVerifyFailsTheStep( "select ( select 1 union all select 2 )" );

	[DockerFact]
	public void StatusOfAFreshDatabaseSaysSoAndDoesNotCreateTheJournal()
		=> _deployment.Value.StatusOfAFreshDatabaseSaysSoAndDoesNotCreateTheJournal();

	[DockerFact]
	public void StatusOfAnEmptyJournalDeploysEverything()
		=> _deployment.Value.StatusOfAnEmptyJournalDeploysEverything();

	[DockerFact]
	public void StatusOfAPartlyDeployedReleaseListsTheRemainingPlansAndDeployThenDoesExactlyThat()
		=> _deployment.Value.StatusOfAPartlyDeployedReleaseListsTheRemainingPlansAndDeployThenDoesExactlyThat();

	[DockerFact]
	public void StatusOfACompletedReleaseHandsOverAndAContinuationBundleSaysWhereItContinuesFrom()
		=> _deployment.Value.StatusOfACompletedReleaseHandsOverAndAContinuationBundleSaysWhereItContinuesFrom();

	[DockerFact]
	public void StatusOfAnUpToDateDatabaseHasNothingToDeploy()
		=> _deployment.Value.StatusOfAnUpToDateDatabaseHasNothingToDeploy();

	[DockerFact]
	public void StatusRefusesAContinuationBundleOnAFreshDatabaseAndWritesNothing()
		=> _deployment.Value.StatusRefusesAContinuationBundleOnAFreshDatabaseAndWritesNothing();

	[DockerFact]
	public void ALockHeldElsewhereRefusesTheDeploymentAndCreatesNothing()
		=> _deployment.Value.ALockHeldElsewhereRefusesTheDeploymentAndCreatesNothing();

	[DockerFact]
	public void TheLockIsReleasedWhileTheDeployingConnectionIsStillOpen()
		=> _deployment.Value.TheLockIsReleasedWhileTheDeployingConnectionIsStillOpen();
}

public sealed class MySqlDeploymentTests( MySqlServer server )
	: MySqlFamilyDeploymentTests( server ), IClassFixture<MySqlServer>;

public sealed class MariaDbDeploymentTests( MariaDbServer server )
	: MySqlFamilyDeploymentTests( server ), IClassFixture<MariaDbServer>;
