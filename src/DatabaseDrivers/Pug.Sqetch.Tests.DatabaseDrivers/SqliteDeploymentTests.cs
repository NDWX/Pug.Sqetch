namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// <see cref="RealDatabaseDeployment"/> against SQLite. The scenarios live there so every provider
/// runs the same ones; what belongs here is how a SQLite database is reached and read back.
/// </summary>
public class SqliteDeploymentTests : IDisposable
{
	private readonly TempDatabase _database = new ();

	private readonly RealDatabaseDeployment _deployment;

	public SqliteDeploymentTests()
		=> _deployment = new RealDatabaseDeployment(
			() => _database.Driver(),
			SqliteJournal.Statements(),
			sql => _database.Query( sql ),
			() => _database.Query(
				"select name from sqlite_master where type = 'table' and name like '%\\_s1' escape '\\' order by name" ) );

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
	public void AFailingRollbackScriptLeavesNoHalfFinishedCompensation()
		=> _deployment.AFailingRollbackScriptLeavesNoHalfFinishedCompensation();
}
