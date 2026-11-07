using Pug.Sqetch.DatabaseDrivers.Sqlite;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// <see cref="RealDatabaseDeployment"/> against SQLite. The scenarios live there so every provider
/// runs the same ones; what belongs here is how a SQLite database is reached and read back.
/// </summary>
public class SqliteDeploymentTests : IDisposable
{
	private readonly TempDatabase _database = new();

	private readonly RealDatabaseDeployment _deployment;

	public SqliteDeploymentTests()
		=> _deployment = new RealDatabaseDeployment(
			() => _database.Driver(),
			BuiltInJournal.Statements( new SqliteDatabaseDriverFactory() ),
			sql => _database.Query( sql ),
			() => _database.Query(
				"select name from sqlite_master where type = 'table' and name like '%\\_s1' escape '\\' order by name"
			)
		);

	public void Dispose() => _database.Dispose();

	[Fact]
	public void AWholeBundleIsAppliedAndJournaled() => _deployment.AWholeBundleIsAppliedAndJournaled();

	[Fact]
	public void TheHostsUtcInstantIsWhatTheJournalRecords() => _deployment.TheHostsUtcInstantIsWhatTheJournalRecords();

	[Fact]
	public void RedeployingTheSameBundleDeploysNothing() => _deployment.RedeployingTheSameBundleDeploysNothing();

	[Fact]
	public void AFailedPlanIsResumedFromWhatTheJournalNames()
		=> _deployment.AFailedPlanIsResumedFromWhatTheJournalNames();

	[Fact]
	public void OnErrorUndoesWhatTheRunAlreadyCommitted() => _deployment.OnErrorUndoesWhatTheRunAlreadyCommitted();

	[Fact]
	public void OnSuccessUndoesTheWholeDeploymentInReverse()
		=> _deployment.OnSuccessUndoesTheWholeDeploymentInReverse();

	[Fact]
	public void RollingBackToAReleaseUndoesTheLaterOnesAndTheyCanBeDeployedAgain()
		=> _deployment.RollingBackToAReleaseUndoesTheLaterOnesAndTheyCanBeDeployedAgain();

	[Fact]
	public void RollingBackAPlanLeavesTheReleaseIncompleteAndRedeployingDeploysOnlyThatPlan()
		=> _deployment.RollingBackAPlanLeavesTheReleaseIncompleteAndRedeployingDeploysOnlyThatPlan();

	[Fact]
	public void RollingBackEverythingLeavesAJournalThatReportsNothingDeployedAndRedeployingDeploysAll()
		=> _deployment.RollingBackEverythingLeavesAJournalThatReportsNothingDeployedAndRedeployingDeploysAll();

	[Fact]
	public void RollingBackAFreshDatabaseFailsTheJournalReadAndDoesNotCreateTheJournal()
		=> _deployment.RollingBackAFreshDatabaseFailsTheJournalReadAndDoesNotCreateTheJournal();

	[Fact]
	public void AFailingRollbackScriptStopsAStandaloneRollbackAndRunningItAgainFinishesIt()
		=> _deployment.AFailingRollbackScriptStopsAStandaloneRollbackAndRunningItAgainFinishesIt();

	[Fact]
	public void ARollbackCutShortInsideAReleaseLeavesItIncompleteAndRedeployingRestoresWhatWasUndone()
		=> _deployment.ARollbackCutShortInsideAReleaseLeavesItIncompleteAndRedeployingRestoresWhatWasUndone();

	[Fact]
	public void AFailingRollbackScriptLeavesNoHalfFinishedCompensation()
		=> _deployment.AFailingRollbackScriptLeavesNoHalfFinishedCompensation();

	[Fact]
	public void AFailingVerifyFailsTheStep()
		=> _deployment.AFailingVerifyFailsTheStep( "select json('x')" );

	[Fact]
	public void StatusOfAFreshDatabaseSaysSoAndDoesNotCreateTheJournal()
		=> _deployment.StatusOfAFreshDatabaseSaysSoAndDoesNotCreateTheJournal();

	[Fact]
	public void StatusOfAnEmptyJournalDeploysEverything()
		=> _deployment.StatusOfAnEmptyJournalDeploysEverything();

	[Fact]
	public void StatusOfAPartlyDeployedReleaseListsTheRemainingPlansAndDeployThenDoesExactlyThat()
		=> _deployment.StatusOfAPartlyDeployedReleaseListsTheRemainingPlansAndDeployThenDoesExactlyThat();

	[Fact]
	public void StatusOfACompletedReleaseHandsOverAndAContinuationBundleSaysWhereItContinuesFrom()
		=> _deployment.StatusOfACompletedReleaseHandsOverAndAContinuationBundleSaysWhereItContinuesFrom();

	[Fact]
	public void StatusOfAnUpToDateDatabaseHasNothingToDeploy()
		=> _deployment.StatusOfAnUpToDateDatabaseHasNothingToDeploy();

	[Fact]
	public void StatusRefusesAContinuationBundleOnAFreshDatabaseAndWritesNothing()
		=> _deployment.StatusRefusesAContinuationBundleOnAFreshDatabaseAndWritesNothing();
}