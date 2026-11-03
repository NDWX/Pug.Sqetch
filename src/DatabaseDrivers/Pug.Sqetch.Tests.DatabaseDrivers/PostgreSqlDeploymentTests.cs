using Pug.Sqetch.DatabaseDrivers.PostgreSql;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// <see cref="RealDatabaseDeployment"/> against PostgreSQL in a container. Running the same
/// scenarios as SQLite is the point: the journaling SQL differs, so the engine's resume decision is
/// being driven by a different server's answers — a boolean from <c>exists(...)</c> here where
/// SQLite returned a count, and a <c>timestamptz</c> where SQLite returned ISO text.
/// </summary>
public class PostgreSqlDeploymentTests : IClassFixture<PostgresDatabase>
{
	private readonly PostgresDatabase _server;

	private readonly Lazy<RealDatabaseDeployment> _deployment;

	public PostgreSqlDeploymentTests( PostgresDatabase server )
	{
		_server = server;

		// lazy, so a run without Docker skips rather than failing to create a database in the
		// constructor, where a skip is no longer available
		_deployment = new Lazy<RealDatabaseDeployment>( () => {
				string connection = _server.CreateDatabase();

				return new RealDatabaseDeployment(
					() => PostgresDatabase.Driver( connection ),
					BuiltInJournal.Statements( new PostgreSqlDatabaseDriverFactory() ),
					sql => PostgresDatabase.Query( connection, sql ),
					() => PostgresDatabase.Query(
						connection,
						"""
						select table_name from information_schema.tables
						where table_schema = 'public' and table_name like '%\_s1'
						order by table_name
						"""
					)
				);
			}
		);
	}

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
	public void OnErrorUndoesWhatTheRunAlreadyCommitted() =>
		_deployment.Value.OnErrorUndoesWhatTheRunAlreadyCommitted();

	[DockerFact]
	public void OnSuccessUndoesTheWholeDeploymentInReverse()
		=> _deployment.Value.OnSuccessUndoesTheWholeDeploymentInReverse();

	[DockerFact]
	public void AFailingRollbackScriptLeavesNoHalfFinishedCompensation()
		=> _deployment.Value.AFailingRollbackScriptLeavesNoHalfFinishedCompensation();

	[DockerFact]
	public void AFailingVerifyFailsTheStep()
		=> _deployment.Value.AFailingVerifyFailsTheStep( "select 1 / count(*) from v_s1" );

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
}