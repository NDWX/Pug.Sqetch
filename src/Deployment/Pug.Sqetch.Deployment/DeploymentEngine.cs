using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Deployment;

/// <summary>
/// Deploys release groups, in the order given, against a database, journaling every event through
/// the project's own SQL (<see cref="JournalingStatements"/>, run by <see cref="StatementJournal"/>)
/// and starting after whatever the journal says is already deployed. The engine does not know where
/// the groups or their scripts come from: the caller determines the groups (the 'sqetch-deploy' CLI
/// reads them out of a bundle manifest) and supplies the SQL through an
/// <see cref="IScriptContentReader"/>. Where deployment starts follows from the journal alone:
/// nothing journaled deploys the groups whole, an <em>incompletely</em> deployed release resumes
/// inside itself (so the groups must contain it), skipping the plans the journal already holds and
/// deploying the rest in the given order, and a <em>completely</em> deployed release hands over to
/// whichever group depends on it — which may be a release the groups do not contain, making this a
/// continuation.
///
/// A deployment opens three kinds of transaction, in order. First the journal's own schema, from
/// the <see cref="JournalingSlot.PrepareJournal"/> slot, committed alone: it must exist before the
/// journal can be read, and DDL auto-commits on some engines, which would implicitly end a
/// transaction shared with anything else. Then both journal queries together, so the resume
/// decision is made from one consistent view rather than two independent round trips. Then the
/// deployment itself, committed at plan or release boundaries per
/// <see cref="DeploymentCommitLevel"/>; a release's completion is always journaled in the
/// same transaction as its last plan. On failure the open transaction is rolled back and
/// the failure rethrown — earlier boundary commits stand.
///
/// Note for the rollback work: rolling a deployment back cannot be "roll back the open
/// transaction", because a maintainer will be able to ask for rollback regardless of commit level
/// and the earlier boundary commits are already durable. It has to be a compensating path that
/// runs the rollback scripts of what this run deployed, in reverse, and journals it through the
/// <c>RollingBack*</c>/<c>RolledBack*</c> slots — which is why transaction bookkeeping here is
/// kept separate from any record of what was deployed.
/// </summary>
public class DeploymentEngine(
	IDatabaseDriver driver,
	JournalingStatements statements,
	IScriptContentReader scripts,
	DeploymentCommitLevel commitLevel,
	IDeploymentListener listener,
	RollbackTrigger rollback = RollbackTrigger.None )
{
	/// <summary>
	/// Deploys <paramref name="groups"/> of <paramref name="project"/> — the name every journaling
	/// statement is given. The groups must form a contiguous lineage: unique names, each naming the
	/// group before it as its dependency. That is checked before the database is touched, and a
	/// violation is an <see cref="ArgumentException"/>, since it is the caller's grouping that is
	/// wrong rather than anything about the database.
	/// </summary>
	public void Deploy( string project, IReadOnlyList<ReleaseGroup> groups )
	{
		ArgumentNullException.ThrowIfNull( project );
		ArgumentNullException.ThrowIfNull( groups );

		EnsureLineage( groups );

		StatementJournal journal = new ( statements, project );

		Prepare( journal );

		(JournaledRelease? latest, List<string>? deployedPlans) = Read( journal, groups );

		if( latest is null )
		{
			Deploy( groups, journal, deployedPlans: null );

			return;
		}

		if( latest.Completed )
		{
			Deploy( groups, journal, latest, deployedPlans: null);

			return;
		}

		// the journal's plans are a set, not a position: resuming by membership is unaffected by
		// the order the bundle lists plans in, which is only partly determined — plans with no
		// dependency between them are ordered by registration, which the journal never records.
		// An empty set resumes at the release's first plan, without journaling its start again.
		Deploy( groups, journal, latest, deployedPlans );
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
		StatementJournal journal, IReadOnlyList<ReleaseGroup> groups )
	{
		IDatabaseTransaction transaction = driver.BeginTransaction();

		try
		{
			JournaledRelease? latest = journal.GetLatestRelease( transaction );

			if( latest is null || latest.Completed )
			{
				transaction.Commit();

				return (latest, null);
			}

			// a partially deployed release must finish before any later release, so a bundle that
			// does not contain it cannot be deployed at all; checked before its plans are queried,
			// since a bundle that cannot be used is not worth the round trip
			if( !groups.Any( group => string.Equals( group.Name, latest.Name, StringComparison.Ordinal ) ) )
				throw new IncompatibleBundleException(
					latest,
					$"{Describe( latest.Name )} is only partially deployed; deploy a bundle that contains it "
					+ $"so its remaining plans deploy{( groups.Count > 0 ? $" before {Describe( groups[0].Name )}" : "" )}." );

			List<string> deployedPlans = journal.GetDeployedPlans( latest.Name, transaction ).ToList();

			transaction.Commit();

			return (latest, deployedPlans);
		}
		catch
		{
			transaction.Rollback();

			throw;
		}
	}

	/// <summary>
	/// Deploys the groups the database has not seen yet. <paramref name="deployedPlans"/> resumes
	/// inside <paramref name="lastGroup"/> — which must then be one of <paramref name="groups"/> —
	/// skipping the plans it names; <paramref name="lastGroup"/> alone hands over to the group
	/// depending on it; neither starts at the bundle's first group.
	/// <paramref name="latestJournaledRelease"/> is diagnostic context for refusals only.
	/// </summary>
	private void Deploy(IReadOnlyList<ReleaseGroup> groups, StatementJournal journal,
		JournaledRelease? latestJournaledRelease = null, IReadOnlyCollection<string>? deployedPlans = null)
	{
		IDatabaseTransaction? transaction = null;
		bool deployedAnything = false;

		// what this run has put beyond reach of a transaction rollback, newest first, so a
		// compensating rollback is a pop: undoing is last-in-first-out by nature, and nesting the
		// plans inside their release makes a release's boundaries structural rather than something
		// rollback has to re-derive by comparing neighbours.
		// A plan moves from pending to committed only when its transaction commits: anything still
		// pending when a transaction rolls back never reached the database and needs no compensation.
		Stack<CommittedRelease> committed = new ();
		List<(ReleaseGroup Release, DeployablePlan Plan)> pending = new ();

		IDatabaseTransaction Transaction() => transaction ??= driver.BeginTransaction();

		void CommitBoundary()
		{
			if( transaction is null )
				return;

			transaction.Commit();
			listener.Committed();

			transaction = null;

			// a transaction never spans releases — each release's deployment ends by committing —
			// so these plans all belong to one release, and at most one frame is opened here
			foreach( (ReleaseGroup release, DeployablePlan plan) in pending )
			{
				CommittedRelease? top = committed.Count > 0 ? committed.Peek() : null;

				if( top is null || !string.Equals( top.Name, release.Name, StringComparison.Ordinal ) )
				{
					top = new CommittedRelease( release.Name, new Stack<DeployablePlan>() );

					committed.Push( top );
				}

				top.Plans.Push( plan );
			}

			pending.Clear();
		}
		
		bool firstGroupFound = false, 
			pseudoReleaseFound = false, 
			lastGroupDeployed = false;
		
		ReleaseGroup lastGroup = null;

		List<(ReleaseGroup, IEnumerable<string>)> groupsToDeploy = new();

		foreach (ReleaseGroup group in groups)
		{
			if (pseudoReleaseFound || (latestJournaledRelease is not null && latestJournaledRelease.Name.Length == 0 && latestJournaledRelease.Completed))
				throw new IncompatibleBundleException(
					latestJournaledRelease,
					$"A pseudo bundle cannot be followed by other releases");

			lastGroup = group;
			pseudoReleaseFound = group.Name.Length == 0;

			// start by working out the first release from the bundle to be deployed
			if (!firstGroupFound)
			{
				// if database has no previous bundle deployment
				if (latestJournaledRelease is null)
				{
					// Bundle must include very first release, which has no dependency.
					// If first release in the bundle has dependency, and therefore doesn't include very first release,
					// bundle is incomplete because first release in the bundle can't have any dependency
					if (group.HasDependency)
						throw new IncompatibleBundleException(
							null,
							$"this bundle continues from {Describe(groups[0].Dependency)} "
							+ "but the database has no deployed releases.");

					groupsToDeploy.Add((group, Array.Empty<string>()));
					firstGroupFound = true;
					continue;
				}

				// database has a previous deployment;
				
				// If last deployed release is found
				if (string.Equals(latestJournaledRelease.Name, group.Name, StringComparison.Ordinal))
				{
					// if last deployed release was completed, skip to next release in bundle
					if (latestJournaledRelease.Completed)
					{
						listener.SkippingRelease(latestJournaledRelease.Name);
						firstGroupFound = true;
						continue;
					}

					// if last deployed release was not yet completed;
					
					// throw exception if deployed plans from that release are not in the bundle,
					// which indicates the release has been changed and therefore
					// the database should be restored from a clean backup
					EnsureDeployedPlansAreInBundleRelease(latestJournaledRelease, deployedPlans!, group);
					
					// otherwise deploy release
					groupsToDeploy.Add((group, deployedPlans!.AsEnumerable()));

					firstGroupFound = true;
					continue;
				}

				// If last deployed release is dependency for next release in bundle
				if (string.Equals(latestJournaledRelease.Name, group.Dependency, StringComparison.Ordinal))
				{
					// If last deployed release was a pseudo-release,
					// ignore subsequent releases in bundle because
					// pseudo-release cannot be followed by any other releases:
					// pseudo-release contains un-released plans and is only possible for test-releases
					// This scenario usually means the project release structure has changed,
					// and therefore database should be restored from a clean backup
					if (latestJournaledRelease.Name.Length == 0)
					{
						listener.SkippingRelease(group.Name);
						continue;
					}
					
					// Otherwise deploy release
					listener.ContinuingFrom(latestJournaledRelease.Name);
					groupsToDeploy.Add((group, Array.Empty<string>()));
					firstGroupFound = true;
					continue;
				}

				// Releases prior to last deployed release are skipped
				listener.SkippingRelease(group.Name);
				continue;
			}

			// Subsequent releases after first to-be-included release are deployed 
			groupsToDeploy.Add((group, Array.Empty<string>()));
		}
		
		// if no new release is found
		if (groupsToDeploy.Count == 0)
		{ 
			// and the last release inspected is not the last release journaled in previous deployment,
			// throw and error
			if (latestJournaledRelease is not null && latestJournaledRelease.Name.Length > 0 && !string.Equals(lastGroup.Name, latestJournaledRelease.Name))
			{
				throw new IncompatibleBundleException(
					latestJournaledRelease,
					$"database is at {Describe( latestJournaledRelease.Name )}, which this bundle neither contains nor continues from." );
			}

			// otherwise, there's just no new release to deploy
			listener.NothingToDeploy();
			return;
		}

		void Deploy(ReleaseGroup release, IEnumerable<string> plansToSkip)
		{
			if (latestJournaledRelease is null || !string.Equals(latestJournaledRelease.Name, release.Name))
			{
				listener.DeployingRelease(release.Name);
				journal.DeployingRelease( release.Name, release.Description, Transaction() );
			}

			// if deployePlans is not null, then we're continuing incomplete release from previous deployment.
			if (plansToSkip is not null)
			{
				// deploy pending plans for the release
				for (int index = 0; index < release.Plans.Count; index++)
				{
					DeployablePlan plan = release.Plans[index];
						
					if (plansToSkip.Contains(plan.Name))
					{
						listener.SkippingPlan(release.Name, plan.Name);
						continue;
					}

					DeployPlan(release.Name, plan, journal, Transaction, listener);
					pending.Add( (release, plan) );

					if (commitLevel == DeploymentCommitLevel.Plan && index < release.Plans.Count - 1)
						CommitBoundary();
				}
			}
			else // deploy pending plans for the next release
			{
				for (int index = 0; index < release.Plans.Count; index++)
				{
					DeployablePlan plan = release.Plans[index];
					DeployPlan(release.Name, plan, journal, Transaction, listener);
					pending.Add( (release, plan) );

					if (commitLevel == DeploymentCommitLevel.Plan && index < release.Plans.Count - 1)
						CommitBoundary();
				}
			}
				
			journal.ReleaseDeployed( release.Name, release.Description, Transaction() );
			listener.ReleaseDeployed(release.Name);

			CommitBoundary();
		}
		
		try
		{
			foreach ((ReleaseGroup release, IEnumerable<string> plansToSkip) in groupsToDeploy)
			{
				Deploy(release, plansToSkip);
			}
		}
		catch( Exception exception )
		{
			transaction?.Rollback();

			// the open transaction took its own plans back; only what committed needs compensating
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
							Transaction().ExecuteStepScript(
								scripts.ReadRollbackScript( release.Name, plan.Name, step.Name ) );
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
			transaction?.Rollback();

			throw;
		}

		listener.RolledBack();
	}

	/// <summary>
	/// The resume and hand-over decisions only look at the first group's dependency and match the
	/// journal by name, so groups that repeat a name or skip a link would be deployed out of lineage
	/// rather than refused.
	/// </summary>
	private static void EnsureLineage( IReadOnlyList<ReleaseGroup> groups )
	{
		HashSet<string> names = new ( StringComparer.Ordinal );

		for( int index = 0; index < groups.Count; index++ )
		{
			ReleaseGroup group = groups[index];

			if( !names.Add( group.Name ) )
				throw new ArgumentException( $"Release '{group.Name}' is listed more than once.", nameof(groups) );

			if( index > 0 && !string.Equals( group.Dependency, groups[index - 1].Name, StringComparison.Ordinal ) )
				throw new ArgumentException(
					$"Release '{group.Name}' does not depend on '{groups[index - 1].Name}', the release preceding it.",
					nameof(groups) );
		}
	}

	private static void EnsureDeployedPlansAreInBundleRelease(JournaledRelease? journaledRelease,
		IReadOnlyCollection<string> deployedPlans, ReleaseGroup bundlePlanGroup)
	{
		IEnumerable<string> missingDeployedPlans = deployedPlans.Except(bundlePlanGroup.Plans.Select(plan => plan.Name));

		if( missingDeployedPlans.Any())
		{
			if( deployedPlans is not null )
				throw new IncompatibleBundleException(
					journaledRelease,
					$"the database has deployed plans of {Describe( journaledRelease.Name )} that this bundle does "
					+ $"not contain: {string.Join( ", ", missingDeployedPlans.Select( plan => $"'{plan}'" ) )}; deploy a "
					+ "bundle that contains them, or restore the database." );
		}
	}

	private void InformSkip( IReadOnlyList<ReleaseGroup> groups, int upTo )
	{
		for( int index = 0; index < upTo; index++ )
			listener.SkippingRelease( groups[index].Name );
	}

	private void DeployPlan(
		string release, DeployablePlan plan, StatementJournal journal,
		Func<IDatabaseTransaction> transaction, IDeploymentListener listener )
	{
		listener.DeployingPlan( release, plan.Name );
		journal.DeployingPlan( release, plan.Name, plan.Description, transaction() );

		foreach( DeployableStep step in plan.Steps )
		{
			listener.DeployingStep( release, plan.Name, step.Name );
			journal.DeployingStep( release, plan.Name, step.Name, step.Description, transaction() );

			string script = scripts.ReadDeployScript( release, plan.Name, step.Name );

			try
			{
				transaction().ExecuteStepScript( script );
			}
			catch( Exception exception )
			{
				throw new StepScriptFailedException( release, plan.Name, step.Name, exception );
			}

			journal.StepDeployed( release, plan.Name, step.Name, step.Description, transaction() );
			listener.StepDeployed( release, plan.Name, step.Name );
		}

		journal.PlanDeployed( release, plan.Name, plan.Description, transaction() );
		listener.PlanDeployed( release, plan.Name );
	}

	/// <summary>Renders a release name for a message; mirrors the CLI listener's wording.</summary>
	private static string Describe( string release )
		=> release.Length == 0 ? "the unreleased-plans pseudo-release" : $"release '{release}'";

	/// <summary>
	/// A release this run committed plans of, and those plans newest first. Only the name is kept:
	/// rollback journals by name, and holding the whole group would tie the record to caller data it
	/// no longer needs.
	/// </summary>
	private sealed record CommittedRelease( string Name, Stack<DeployablePlan> Plans );

	/// <summary>
	/// The group deployment starts at and, when that group is being resumed, the names of its
	/// plans the journal already holds — null when the group is entered fresh. A resumed release
	/// does not have its start journaled again: the run that began it did that.
	/// </summary>
	private sealed record StartPoint( int Group, IReadOnlySet<string>? DeployedPlans );
}
