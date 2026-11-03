using Microsoft.Data.SqlClient;

using Pug.Sqetch.DatabaseDrivers.SqlServer;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// <see cref="RealDatabaseDeployment"/> against SQL Server in a container. SQL Server runs DDL inside
/// a transaction, so every scenario holds as on PostgreSQL; the journal's 'completed' column arrives
/// here as an int from 'case when exists(...)', and 'plan' is quoted, being reserved in T-SQL.
/// </summary>
public class SqlServerDeploymentTests : IClassFixture<SqlServerDatabase>
{
	private readonly Lazy<RealDatabaseDeployment> _deployment;

	public SqlServerDeploymentTests( SqlServerDatabase server )
	{
		// lazy, so a run without Docker skips rather than failing to create a database in the
		// constructor, where a skip is no longer available
		_deployment = new Lazy<RealDatabaseDeployment>( () => {
				string connection = server.CreateDatabase();

				return new RealDatabaseDeployment(
					() => SqlServerDatabase.Driver( connection ),
					BuiltInJournal.Statements( new SqlServerDatabaseDriverFactory() ),
					sql => SqlServerDatabase.Query( connection, sql ),
					() => SqlServerDatabase.Query(
						connection,
						"select name from sys.tables where name like '%[_]s1' order by name"
					),
					name => $"[{name}]",
					endPooledSessions: SqlConnection.ClearAllPools
				);
			}
		);
	}

	[DockerAmd64Fact]
	public void AWholeBundleIsAppliedAndJournaled() => _deployment.Value.AWholeBundleIsAppliedAndJournaled();

	[DockerAmd64Fact]
	public void TheHostsUtcInstantIsWhatTheJournalRecords()
		=> _deployment.Value.TheHostsUtcInstantIsWhatTheJournalRecords();

	[DockerAmd64Fact]
	public void RedeployingTheSameBundleDeploysNothing() => _deployment.Value.RedeployingTheSameBundleDeploysNothing();

	[DockerAmd64Fact]
	public void AFailedPlanIsResumedFromWhatTheJournalNames()
		=> _deployment.Value.AFailedPlanIsResumedFromWhatTheJournalNames();

	[DockerAmd64Fact]
	public void OnErrorUndoesWhatTheRunAlreadyCommitted() => _deployment.Value.OnErrorUndoesWhatTheRunAlreadyCommitted();

	[DockerAmd64Fact]
	public void OnSuccessUndoesTheWholeDeploymentInReverse()
		=> _deployment.Value.OnSuccessUndoesTheWholeDeploymentInReverse();

	[DockerAmd64Fact]
	public void AFailingRollbackScriptLeavesNoHalfFinishedCompensation()
		=> _deployment.Value.AFailingRollbackScriptLeavesNoHalfFinishedCompensation();

	[DockerAmd64Fact]
	public void AFailingVerifyFailsTheStep()
		=> _deployment.Value.AFailingVerifyFailsTheStep( "select 1 / count(*) from v_s1" );

	[DockerAmd64Fact]
	public void StatusOfAFreshDatabaseSaysSoAndDoesNotCreateTheJournal()
		=> _deployment.Value.StatusOfAFreshDatabaseSaysSoAndDoesNotCreateTheJournal();

	[DockerAmd64Fact]
	public void StatusOfAnEmptyJournalDeploysEverything()
		=> _deployment.Value.StatusOfAnEmptyJournalDeploysEverything();

	[DockerAmd64Fact]
	public void StatusOfAPartlyDeployedReleaseListsTheRemainingPlansAndDeployThenDoesExactlyThat()
		=> _deployment.Value.StatusOfAPartlyDeployedReleaseListsTheRemainingPlansAndDeployThenDoesExactlyThat();

	[DockerAmd64Fact]
	public void StatusOfACompletedReleaseHandsOverAndAContinuationBundleSaysWhereItContinuesFrom()
		=> _deployment.Value.StatusOfACompletedReleaseHandsOverAndAContinuationBundleSaysWhereItContinuesFrom();

	[DockerAmd64Fact]
	public void StatusOfAnUpToDateDatabaseHasNothingToDeploy()
		=> _deployment.Value.StatusOfAnUpToDateDatabaseHasNothingToDeploy();

	[DockerAmd64Fact]
	public void StatusRefusesAContinuationBundleOnAFreshDatabaseAndWritesNothing()
		=> _deployment.Value.StatusRefusesAContinuationBundleOnAFreshDatabaseAndWritesNothing();

	[DockerAmd64Fact]
	public void ALockHeldElsewhereRefusesTheDeploymentAndCreatesNothing()
		=> _deployment.Value.ALockHeldElsewhereRefusesTheDeploymentAndCreatesNothing();

	[DockerAmd64Fact]
	public void TheLockIsReleasedWhileTheDeployingConnectionIsStillOpen()
		=> _deployment.Value.TheLockIsReleasedWhileTheDeployingConnectionIsStillOpen();

	[DockerAmd64Fact]
	public void AcquireLockAnswersZeroNotNullWhileTheLockIsHeldElsewhere()
		=> _deployment.Value.AcquireLockAnswersFalseWhileTheLockIsHeldElsewhere();
}