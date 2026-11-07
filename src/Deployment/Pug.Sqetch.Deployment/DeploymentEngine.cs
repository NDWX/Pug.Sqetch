using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Deployment;

/// <summary>
/// Deploys release groups, in the order given, against a database, journaling every event through
/// the project's own SQL (<see cref="JournalingStatements"/>, run by <see cref="StatementJournal"/>)
/// and starting after whatever the journal says is already deployed. The engine does not know where
/// the groups or their scripts come from: the caller determines the groups (the 'sqetch-deploy' CLI
/// reads them out of a bundle manifest) and supplies the SQL through an
/// <see cref="IScriptContentReader"/>. Where deployment starts follows from the journal alone, as
/// <see cref="DeploymentPlanner"/> decides — the same decision <see cref="DeploymentForecaster"/>
/// reports without deploying.
///
/// When the journaling SQL sets the optional deployment lock, a deployment first takes it, in a
/// transaction committed alone, and gives it back in another when it ends, however it ends; a lock
/// held elsewhere refuses the deployment (<see cref="DeploymentLockedException"/>). Between them, a
/// deployment opens three kinds of transaction, in order. First the journal's own schema, from
/// the <see cref="JournalingSlot.PrepareJournal"/> slot, committed alone: it must exist before the
/// journal can be read, and DDL auto-commits on some engines, which would implicitly end a
/// transaction shared with anything else. Then both journal queries together, so the resume
/// decision is made from one consistent view rather than two independent round trips. Then the
/// deployment itself, committed at plan or release boundaries per
/// <see cref="DeploymentCommitLevel"/>; a release's completion is always journaled in the
/// same transaction as its last plan. On failure the open transaction is rolled back and
/// the failure rethrown — earlier boundary commits stand.
///
/// Rolling back is never "roll back the open transaction": the earlier boundary commits are already
/// durable, whatever the commit level. It is a compensating pass running the rollback scripts in
/// reverse and journaling through the <c>RollingBack*</c>/<c>RolledBack*</c> slots — either of what
/// this run committed (<see cref="RollbackTrigger"/>), or, standalone (<see cref="RollBack"/>), of
/// what the journal says is deployed, as <see cref="RollbackPlanner"/> decides. A standalone
/// rollback never provisions the journal: a database without one fails the journal read, so naming
/// the wrong database creates nothing there.
/// </summary>
public class DeploymentEngine(
	IDatabaseDriver driver,
	JournalingStatements statements,
	IScriptContentReader scripts,
	DeploymentCommitLevel commitLevel,
	IDeploymentListener listener,
	RollbackTrigger rollback = RollbackTrigger.None
)
{
	/// <summary>
	/// Deploys <paramref name="groups"/> of <paramref name="project"/> — the name every journaling
	/// statement is given. The groups must form a contiguous lineage: unique names, each naming the
	/// group before it as its dependency. That is checked before the database is touched, and a
	/// violation is an <see cref="ArgumentException"/>, since it is the caller's grouping that is
	/// wrong rather than anything about the database.
	///
	/// With <paramref name="verifySteps"/>, each step's verify script runs right after its deploy
	/// script, in the same transaction, and an error from it fails the step
	/// (<see cref="StepVerificationFailedException"/>) like an error from the deploy script would. A
	/// blank verify script is not run. Turning verification off is for emergencies.
	/// </summary>
	public void Deploy( string project, IReadOnlyList<ReleaseGroup> groups, bool verifySteps = true )
	{
		ArgumentNullException.ThrowIfNull( project );
		ArgumentNullException.ThrowIfNull( groups );

		DeploymentPlanner.EnsureLineage( groups );

		StatementJournal journal = new( statements, project );

		bool locked = false;

		try
		{
			// marked held the moment the database says so, so a commit or listener failing after that
			// still releases it
			AcquireLock( journal, () => locked = true );

			Deploy( journal, groups, verifySteps );
		}
		finally
		{
			if( locked )
				ReleaseLock( journal );
		}
	}

	private void Deploy( StatementJournal journal, IReadOnlyList<ReleaseGroup> groups, bool verifySteps )
	{
		Prepare( journal );

		( JournaledRelease? latest, List<string>? deployedPlans ) = Read( journal, groups );

		if( latest is null )
		{
			Deploy( groups, journal, verifySteps, deployedPlans: null );

			return;
		}

		if( latest.Completed )
		{
			Deploy( groups, journal, verifySteps, latest, deployedPlans: null );

			return;
		}

		// the journal's plans are a set, not a position: resuming by membership is unaffected by
		// the order the bundle lists plans in, which is only partly determined — plans with no
		// dependency between them are ordered by registration, which the journal never records.
		// An empty set resumes at the release's first plan, without journaling its start again.
		Deploy( groups, journal, verifySteps, latest, deployedPlans );
	}

	/// <summary>
	/// Undoes what the journal says <paramref name="project"/> has deployed, as far as
	/// <paramref name="target"/> asks, running the rollback scripts <paramref name="groups"/> supply —
	/// releases newest first, their plans and each plan's steps in reverse — and committing at plan or
	/// release boundaries per the commit level. Like <see cref="Deploy(string, IReadOnlyList{ReleaseGroup}, bool)"/>
	/// it takes the deployment lock when one is set, then reads the journal in one transaction — but
	/// never runs <see cref="JournalingSlot.PrepareJournal"/>: undoing presumes a journal, so a database
	/// without one fails that read (<see cref="JournalingStatementException"/>) rather than gaining one,
	/// which is what naming the wrong database should do. Nothing to undo is reported to the listener
	/// and is not a failure.
	///
	/// A target or groups that do not fit are refused before anything is undone
	/// (<see cref="RollbackRefusedException"/>, <see cref="IncompatibleBundleException"/>); a failing
	/// rollback script is <see cref="RollbackFailedException"/>, with what committed before it standing,
	/// so running the same rollback again finishes it.
	/// </summary>
	public void RollBack( string project, IReadOnlyList<ReleaseGroup> groups, RollbackTarget target )
	{
		ArgumentNullException.ThrowIfNull( project );
		ArgumentNullException.ThrowIfNull( groups );
		ArgumentNullException.ThrowIfNull( target );

		DeploymentPlanner.EnsureLineage( groups );
		RollbackPlanner.EnsureTarget( groups, target );

		StatementJournal journal = new( statements, project );

		bool locked = false;

		try
		{
			AcquireLock( journal, () => locked = true );

			RollbackForecast forecast = ReadForRollback( journal, groups, target );

			if( forecast.NothingToRollBack )
			{
				listener.NothingToRollBack();

				return;
			}

			// pushed oldest first, so popping undoes the newest release, and its newest plan, first
			Stack<CommittedRelease> deployed = new(
				forecast.Releases
					.Reverse()
					.Select( planned => new CommittedRelease( planned.Group.Name, new Stack<DeployablePlan>( planned.Plans ) ) )
			);

			RollBack( journal, deployed, Describe( target, forecast ) );
		}
		finally
		{
			if( locked )
				ReleaseLock( journal );
		}
	}

	/// <summary>
	/// Reads where the journal is and the plans it holds for every release the rollback would undo, in
	/// one transaction, and plans the rollback from them.
	/// </summary>
	private RollbackForecast ReadForRollback(
		StatementJournal journal,
		IReadOnlyList<ReleaseGroup> groups,
		RollbackTarget target
	)
	{
		IDatabaseTransaction transaction = driver.BeginTransaction();

		JournaledRelease? latest;
		Dictionary<string, IReadOnlyCollection<string>> deployedPlans = new( StringComparer.Ordinal );

		try
		{
			latest = journal.GetLatestRelease( transaction );

			foreach( ReleaseGroup release in RollbackPlanner.ReleasesToRollBack( groups, latest, target ) )
				deployedPlans[release.Name] = journal.GetDeployedPlans( release.Name, transaction ).ToList();

			transaction.Commit();
		}
		catch
		{
			RollBackQuietly( transaction );

			throw;
		}

		return RollbackPlanner.Plan( groups, latest, deployedPlans, target );
	}

	/// <summary>Rolls a failed transaction back, keeping the failure being reported if that fails too.</summary>
	private static void RollBackQuietly( IDatabaseTransaction transaction )
	{
		try
		{
			transaction.Rollback();
		}
		catch
		{
			// the failure that led here is the one reported
		}
	}

	/// <summary>Why a standalone rollback runs, as the listener reports it.</summary>
	private static string Describe( RollbackTarget target, RollbackForecast forecast )
		=> target.Kind switch
		{
			RollbackTargetKind.ToRelease => $"everything deployed after {DeploymentPlanner.Describe( target.Release )}",
			RollbackTargetKind.All => "everything deployed",
			_ => $"{( forecast.Releases[0].Plans.Count == 1 ? "plan" : "plans" )} " +
				$"{string.Join( ", ", forecast.Releases[0].Plans.Select( plan => $"'{plan.Name}'" ) )} of " +
				DeploymentPlanner.Describe( forecast.Releases[0].Group.Name )
		};

	/// <summary>
	/// Takes the database's deployment lock, when the journaling SQL sets one, before anything else —
	/// even the journal's provisioning, whose DDL two deployments must not race on. Committed on its
	/// own and, like provisioning, not reported as a commit: the lock is a session's, so it outlives
	/// this transaction and holds until <see cref="ReleaseLock"/> or the session ends.
	/// <paramref name="held"/> is called as soon as the lock is taken; nothing is called when no lock
	/// is set, and a lock held elsewhere refuses the deployment.
	/// </summary>
	private void AcquireLock( StatementJournal journal, Action held )
	{
		// no transaction at all without a lock, so an unguarded deployment runs exactly as it did
		if( !statements.IsSet( JournalingSlot.AcquireLock ) )
			return;

		IDatabaseTransaction transaction = driver.BeginTransaction();

		try
		{
			if( journal.AcquireLock( transaction ) != true )
				throw new DeploymentLockedException();

			held();

			transaction.Commit();
		}
		catch
		{
			transaction.Rollback();

			throw;
		}

		listener.LockAcquired();
	}

	/// <summary>
	/// Gives the deployment lock back, however the deployment ended. A failure here never replaces
	/// the deployment's outcome — success or the failure being reported — and needs no remedy beyond
	/// a warning: the lock is a session's, so it ends with the connection anyway.
	/// </summary>
	private void ReleaseLock( StatementJournal journal )
	{
		IDatabaseTransaction? transaction = null;

		try
		{
			transaction = driver.BeginTransaction();

			journal.ReleaseLock( transaction );

			transaction.Commit();
		}
		catch( Exception exception )
		{
			try
			{
				transaction?.Rollback();
			}
			catch
			{
				// the release failure below is what is reported
			}

			listener.LockNotReleased( exception );
		}
	}

	/// <summary>
	/// Runs the journal's provisioning statements and commits them on their own, before anything
	/// reads or writes the journal. It runs on every deployment, so the project's SQL has to be
	/// safe against an already-provisioned database. The listener is deliberately not told about
	/// this commit: it is not deployment progress, and reporting it would have an up-to-date
	/// database announce a commit before saying it had nothing to do.
	/// </summary>
	private void Prepare( StatementJournal journal )
	{
		IDatabaseTransaction transaction = driver.BeginTransaction();

		try
		{
			journal.PrepareJournal( transaction );

			transaction.Commit();
		}
		catch
		{
			transaction.Rollback();

			throw;
		}
	}

	/// <summary>
	/// Determine latest release (being) deployed, and include it's deployed plans if partially deployed.
	/// </summary>
	private (JournaledRelease? Latest, List<string>? DeployedPlans) Read(
		StatementJournal journal,
		IReadOnlyList<ReleaseGroup> groups
	)
	{
		IDatabaseTransaction transaction = driver.BeginTransaction();

		try
		{
			JournaledRelease? latest = journal.GetLatestRelease( transaction );

			if( latest is null || latest.Completed )
			{
				transaction.Commit();

				return ( latest, null );
			}

			DeploymentPlanner.EnsureResumable( latest, groups );

			List<string> deployedPlans = journal.GetDeployedPlans( latest.Name, transaction ).ToList();

			transaction.Commit();

			return ( latest, deployedPlans );
		}
		catch
		{
			transaction.Rollback();

			throw;
		}
	}

	/// <summary>
	/// Deploys the groups the database has not seen yet, as <see cref="DeploymentPlanner"/> decides
	/// from <paramref name="latestJournaledRelease"/> and, when that release is incomplete,
	/// <paramref name="deployedPlans"/>; reports the planner's notes to the listener first, in order.
	/// </summary>
	private void Deploy(
		IReadOnlyList<ReleaseGroup> groups,
		StatementJournal journal,
		bool verifySteps,
		JournaledRelease? latestJournaledRelease = null,
		IReadOnlyCollection<string>? deployedPlans = null
	)
	{
		IDatabaseTransaction? transaction = null;

		// what this run has put beyond reach of a transaction rollback, newest first, so a
		// compensating rollback is a pop: undoing is last-in-first-out by nature, and nesting the
		// plans inside their release makes a release's boundaries structural rather than something
		// rollback has to re-derive by comparing neighbours.
		// A plan moves from pending to committed only when its transaction commits: anything still
		// pending when a transaction rolls back never reached the database and needs no compensation.
		Stack<CommittedRelease> committed = new();
		List<(ReleaseGroup Release, DeployablePlan Plan)> pending = new();

		// the plan being deployed and the steps whose deploy script started, which only an engine that
		// commits DDL by itself needs: there a failed transaction may have kept them
		(ReleaseGroup Release, DeployablePlan Plan, List<DeployableStep> Started)? inProgress = null;

		IDatabaseTransaction Transaction() => transaction ??= driver.BeginTransaction();

		// a transaction never spans releases — each release's deployment ends by committing — so the
		// plans recorded between two commits all belong to one release, and at most one frame opens
		void Record( ReleaseGroup release, DeployablePlan plan )
		{
			CommittedRelease? top = committed.Count > 0 ? committed.Peek() : null;

			if( top is null || !string.Equals( top.Name, release.Name, StringComparison.Ordinal ) )
			{
				top = new CommittedRelease( release.Name, new Stack<DeployablePlan>() );

				committed.Push( top );
			}

			top.Plans.Push( plan );
		}

		void DeployOne( ReleaseGroup release, DeployablePlan plan )
		{
			inProgress = ( release, plan, [ ] );

			DeployPlan( release.Name, plan, journal, Transaction, listener, inProgress.Value.Started, verifySteps );

			pending.Add( ( release, plan ) );
			inProgress = null;
		}

		void CommitBoundary()
		{
			if( transaction is null )
				return;

			transaction.Commit();
			listener.Committed();

			transaction = null;

			foreach( ( ReleaseGroup release, DeployablePlan plan ) in pending )
				Record( release, plan );

			pending.Clear();
		}

		DeploymentForecast forecast = DeploymentPlanner.Plan( groups, latestJournaledRelease, deployedPlans );

		foreach( ForecastNote note in forecast.Notes )
			if( note.Kind == ForecastNoteKind.ContinuingFrom )
				listener.ContinuingFrom( note.Release );
			else
				listener.SkippingRelease( note.Release );

		if( forecast.NothingToDeploy )
		{
			listener.NothingToDeploy();

			return;
		}

		void Deploy( PlannedRelease planned )
		{
			ReleaseGroup release = planned.Group;

			// a resumed release had its start journaled by the run that began it
			if( !planned.Resumed )
			{
				listener.DeployingRelease( release.Name );
				journal.DeployingRelease( release.Name, release.Description, Transaction() );
			}

			for( int index = 0; index < release.Plans.Count; index++ )
			{
				DeployablePlan plan = release.Plans[index];

				if( planned.SkippedPlans.Contains( plan.Name ) )
				{
					listener.SkippingPlan( release.Name, plan.Name );

					continue;
				}

				DeployOne( release, plan );

				if( commitLevel == DeploymentCommitLevel.Plan && index < release.Plans.Count - 1 )
					CommitBoundary();
			}

			journal.ReleaseDeployed( release.Name, release.Description, Transaction() );
			listener.ReleaseDeployed( release.Name );

			CommitBoundary();
		}

		try
		{
			foreach( PlannedRelease planned in forecast.Releases )
				Deploy( planned );
		}
		catch( Exception exception )
		{
			transaction?.Rollback();

			// the open transaction took its own plans back, so only what committed needs compensating —
			// unless the engine commits DDL by itself, when the rollback may have taken nothing back.
			// There what the transaction held counts as committed: its whole plans, and the failed plan
			// as far as it got, the failing step included since it may have half applied
			if( driver.CommitsDdlImplicitly && ( pending.Count > 0 || inProgress is { Started.Count: > 0 } ) )
			{
				listener.Committed();

				foreach( ( ReleaseGroup release, DeployablePlan plan ) in pending )
					Record( release, plan );

				if( inProgress is { Started.Count: > 0 } partway )
					Record( partway.Release, partway.Plan with { Steps = partway.Started } );
			}

			pending.Clear();

			if( rollback == RollbackTrigger.OnError && committed.Count > 0 )
				RollBack( journal, committed, $"deployment failed: {exception.Message}" );

			throw;
		}

		if( rollback == RollbackTrigger.OnSuccess && committed.Count > 0 )
			RollBack( journal, committed, "deployment succeeded; rolling back as requested" );
	}

	/// <summary>
	/// Undoes what <paramref name="committed"/> holds, popping it: releases newest first, each
	/// release's plans newest first, and each plan's steps in reverse. Running each step's rollback
	/// script and journaling through the RollingBack/RolledBack slots. This is a compensating pass,
	/// not a transaction rollback — by the time it runs the work is committed, which is why it is
	/// the only way to undo a deployment that committed at plan boundaries.
	///
	/// It consumes the record as it goes, so whatever remains on failure is exactly what was not
	/// compensated.
	/// </summary>
	private void RollBack( StatementJournal journal, Stack<CommittedRelease> committed, string reason )
	{
		listener.RollingBack( reason );

		IDatabaseTransaction? transaction = null;

		IDatabaseTransaction Transaction() => transaction ??= driver.BeginTransaction();

		void CommitBoundary()
		{
			if( transaction is null )
				return;

			transaction.Commit();

			transaction = null;
		}

		try
		{
			while( committed.Count > 0 )
			{
				CommittedRelease release = committed.Pop();

				listener.RollingBackRelease( release.Name );
				journal.RollingBackRelease( release.Name, Transaction() );

				while( release.Plans.Count > 0 )
				{
					DeployablePlan plan = release.Plans.Pop();

					listener.RollingBackPlan( release.Name, plan.Name );
					journal.RollingBackPlan( release.Name, plan.Name, Transaction() );

					// the steps are the caller's data, not this run's state, so they are
					// traversed backwards rather than consumed
					foreach( DeployableStep step in plan.Steps.Reverse() )
					{
						journal.RollingBackStep( release.Name, plan.Name, step.Name, Transaction() );

						try
						{
							Transaction()
								.ExecuteStepScript(
									scripts.ReadRollbackScript( release.Name, plan.Name, step.Name )
								);
						}
						catch( Exception exception )
						{
							throw new RollbackFailedException( release.Name, plan.Name, step.Name, exception );
						}

						journal.RolledBackStep( release.Name, plan.Name, step.Name, Transaction() );
					}

					journal.RolledBackPlan( release.Name, plan.Name, Transaction() );

					if( release.Plans.Count > 0 && commitLevel == DeploymentCommitLevel.Plan )
						CommitBoundary();
				}

				journal.RolledBackRelease( release.Name, Transaction() );

				CommitBoundary();
			}
		}
		catch
		{
			// a rollback failing too (a connection gone) must not replace what stopped the undoing,
			// which is what the exit code and the person repairing it go by
			if( transaction is not null )
				RollBackQuietly( transaction );

			throw;
		}

		listener.RolledBack();
	}

	private void DeployPlan(
		string release,
		DeployablePlan plan,
		StatementJournal journal,
		Func<IDatabaseTransaction> transaction,
		IDeploymentListener listener,
		List<DeployableStep> started,
		bool verifySteps
	)
	{
		listener.DeployingPlan( release, plan.Name );
		journal.DeployingPlan( release, plan.Name, plan.Description, transaction() );

		foreach( DeployableStep step in plan.Steps )
		{
			listener.DeployingStep( release, plan.Name, step.Name );
			journal.DeployingStep( release, plan.Name, step.Name, step.Description, transaction() );

			string script = scripts.ReadDeployScript( release, plan.Name, step.Name );

			started.Add( step );

			try
			{
				transaction().ExecuteStepScript( script );
			}
			catch( Exception exception )
			{
				throw new StepScriptFailedException( release, plan.Name, step.Name, exception );
			}

			if( verifySteps )
				VerifyStep( release, plan.Name, step.Name, transaction(), listener );

			journal.StepDeployed( release, plan.Name, step.Name, step.Description, transaction() );
			listener.StepDeployed( release, plan.Name, step.Name );
		}

		journal.PlanDeployed( release, plan.Name, plan.Description, transaction() );
		listener.PlanDeployed( release, plan.Name );
	}

	/// <summary>
	/// Runs the step's verify script in the transaction its deploy script ran in, before the step is
	/// journaled as deployed, so a failing check leaves the step undeployed in the journal too. Only an
	/// error fails it: Sqetch reads no results. A blank script is skipped, since new steps start with
	/// an empty one and some providers refuse an empty command.
	/// </summary>
	private void VerifyStep(
		string release,
		string plan,
		string step,
		IDatabaseTransaction transaction,
		IDeploymentListener listener
	)
	{
		string script = scripts.ReadVerifyScript( release, plan, step );

		if( string.IsNullOrWhiteSpace( script ) )
			return;

		listener.VerifyingStep( release, plan, step );

		try
		{
			transaction.ExecuteStepScript( script );
		}
		catch( Exception exception )
		{
			throw new StepVerificationFailedException( release, plan, step, exception );
		}
	}

	/// <summary>
	/// A release this run committed plans of, and those plans newest first. Only the name is kept:
	/// rollback journals by name, and holding the whole group would tie the record to caller data it
	/// no longer needs.
	/// </summary>
	private sealed record CommittedRelease( string Name, Stack<DeployablePlan> Plans );
}