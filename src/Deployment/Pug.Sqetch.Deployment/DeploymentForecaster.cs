using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Deployment;

/// <summary>
/// Says what <see cref="DeploymentEngine.Deploy"/> would do with some release groups against a
/// database, without doing any of it: it reads the journal and hands what it read to
/// <see cref="DeploymentPlanner"/>, the decision the engine itself uses.
///
/// It only reads. <see cref="JournalingSlot.PrepareJournal"/> never runs — it would create the
/// journal on a database never deployed to — so the optional <see cref="JournalingSlot.JournalExists"/>
/// query says whether there is a journal to read, and statements lacking it are refused before the
/// database is touched (<see cref="JournalingStatementsNotSetException"/>): without it a database never
/// deployed to and a journal query that failed look alike. The queries run in one transaction, so the
/// answer comes from one consistent view, and that transaction is always rolled back, never
/// committed.
/// </summary>
public sealed class DeploymentForecaster( IDatabaseDriver driver, JournalingStatements statements )
{
	/// <summary>What <see cref="JournalingSlot.JournalExists"/> is needed for, as the refusal words it.</summary>
	public const string JournalExistsPurpose = "status needs to recognise a database with no journal";

	/// <summary>
	/// Forecasts deploying <paramref name="groups"/> of <paramref name="project"/>. The groups must
	/// form a contiguous lineage, as for deploying (<see cref="ArgumentException"/> otherwise); groups
	/// that do not fit the journal are refused with <see cref="IncompatibleBundleException"/>, exactly
	/// as deploying them would be.
	/// </summary>
	public DeploymentStatus Forecast( string project, IReadOnlyList<ReleaseGroup> groups )
	{
		ArgumentNullException.ThrowIfNull( project );
		ArgumentNullException.ThrowIfNull( groups );

		DeploymentPlanner.EnsureLineage( groups );

		if( !statements.IsSet( JournalingSlot.JournalExists ) )
			throw new JournalingStatementsNotSetException( [ nameof(JournalingSlot.JournalExists) ], JournalExistsPurpose );

		StatementJournal journal = new( statements, project );

		IDatabaseTransaction transaction = driver.BeginTransaction();

		DeploymentStatus status;

		try
		{
			status = Read( journal, transaction, groups );
		}
		catch
		{
			// the failure is the answer; a rollback failing after it (a transaction the server already
			// ended, a broken connection) must not replace it
			try
			{
				transaction.Rollback();
			}
			catch
			{
				// reported through the original failure
			}

			throw;
		}

		// reading is all this did, and nothing it ran is to be kept
		transaction.Rollback();

		return status;
	}

	private static DeploymentStatus Read(
		StatementJournal journal,
		IDatabaseTransaction transaction,
		IReadOnlyList<ReleaseGroup> groups
	)
	{
		if( journal.JournalExists( transaction ) != true )
			return new DeploymentStatus( JournalExists: false, DeploymentPlanner.Plan( groups, null, null ) );

		JournaledRelease? latest = journal.GetLatestRelease( transaction );
		IReadOnlyList<string>? deployedPlans = null;

		if( latest is { Completed: false } )
		{
			DeploymentPlanner.EnsureResumable( latest, groups );

			deployedPlans = journal.GetDeployedPlans( latest.Name, transaction );
		}

		return new DeploymentStatus( JournalExists: true, DeploymentPlanner.Plan( groups, latest, deployedPlans ) );
	}
}

/// <summary>
/// Where a database stands and what deploying would do there. <paramref name="JournalExists"/> false
/// is a database never deployed to; otherwise <see cref="DeploymentForecast.Journaled"/> is the
/// release the journal is at, null when the journal exists but holds nothing.
/// </summary>
public sealed record DeploymentStatus( bool JournalExists, DeploymentForecast Forecast );
