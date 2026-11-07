namespace Pug.Sqetch.Deployment;

/// <summary>
/// The rollback decision: given the release groups a caller has (a bundle's), what the journal says
/// and a <see cref="RollbackTarget"/>, which releases and plans are undone. Pure — no database, no
/// listener — like <see cref="DeploymentPlanner"/>.
///
/// The journal decides <em>what</em> is deployed, by membership: the release it is at, and for each
/// release the set of plans it holds. The groups decide the <em>order</em>: undoing runs their order
/// backwards, which is safe because it is a dependency order, and plans with no dependency between them
/// are independent, so the order the journal saw them in does not matter. The groups also supply the
/// rollback scripts, so they must contain every release and journaled plan to undo; otherwise the
/// rollback is refused with <see cref="IncompatibleBundleException"/>. A target that does not fit is
/// <see cref="RollbackRefusedException"/>. Both are raised before anything is undone.
///
/// Releases before the journal's are taken as completely deployed — a lineage deploys in order — but
/// their plans are still read from the journal, so a plan never deployed is never undone.
/// </summary>
public static class RollbackPlanner
{
	/// <summary>
	/// Refuses a target no journal could make sense of with these groups: a release outside their
	/// lineage, or a plan none of them holds. Needs no database, so a typo is refused before anything
	/// connects.
	/// </summary>
	public static void EnsureTarget( IReadOnlyList<ReleaseGroup> groups, RollbackTarget target )
	{
		ArgumentNullException.ThrowIfNull( groups );
		ArgumentNullException.ThrowIfNull( target );

		if( target.Kind == RollbackTargetKind.ToRelease &&
			IndexOf( groups, target.Release ) < 0 &&
			!IsContinuationBase( groups, target.Release ) )
			throw new RollbackRefusedException(
				$"{DeploymentPlanner.Describe( target.Release )} is not in this bundle's lineage, so there is " +
				"nothing to roll back to."
			);

		if( target.Kind != RollbackTargetKind.Plans )
			return;

		List<string> unknown = target.Plans.Where( plan => ReleaseOf( groups, plan ) < 0 ).ToList();

		if( unknown.Count > 0 )
			throw new RollbackRefusedException(
				$"this bundle has no {( unknown.Count == 1 ? "plan" : "plans" )} {Quote( unknown )}."
			);
	}

	/// <summary>
	/// The groups whose deployed plans are undone, newest first — the releases whose plans the journal
	/// must be asked for. Empty when there is nothing to roll back.
	/// </summary>
	public static IReadOnlyList<ReleaseGroup> ReleasesToRollBack(
		IReadOnlyList<ReleaseGroup> groups,
		JournaledRelease? latest,
		RollbackTarget target
	)
	{
		ArgumentNullException.ThrowIfNull( groups );
		ArgumentNullException.ThrowIfNull( target );

		EnsureTarget( groups, target );

		if( latest is null )
			return [ ];

		int newest = IndexOf( groups, latest.Name );

		if( newest < 0 )
		{
			// the database is at the release the bundle continues from: every release and plan the
			// bundle holds is after it, so rolling back to one of them, to it, or any of their plans has
			// nothing to do
			if( target.Kind != RollbackTargetKind.All && IsContinuationBase( groups, latest.Name ) )
				return [ ];

			throw new IncompatibleBundleException(
				latest,
				$"database is at {DeploymentPlanner.Describe( latest.Name )}, which this bundle does not contain; " +
				"roll back with a bundle that contains it."
			);
		}

		int oldest;

		switch( target.Kind )
		{
			case RollbackTargetKind.ToRelease:
				// the continuation base sits just before the first group
				oldest = IsContinuationBase( groups, target.Release ) ? 0 : IndexOf( groups, target.Release ) + 1;

			break;

			case RollbackTargetKind.All:
				if( groups.Count > 0 && groups[0].HasDependency )
					throw new IncompatibleBundleException(
						latest,
						$"this bundle continues from {DeploymentPlanner.Describe( groups[0].Dependency )}, so it " +
						"cannot roll back everything; use a bundle holding every deployed release."
					);

				oldest = 0;

			break;

			default:
				// a plan of an earlier release cannot go while the releases after it stay
				List<string> earlier = target.Plans.Where( plan => ReleaseOf( groups, plan ) < newest ).ToList();

				if( earlier.Count > 0 )
					throw new RollbackRefusedException(
						$"{( earlier.Count == 1 ? "plan" : "plans" )} {Quote( earlier )} belong to a release before " +
						$"{DeploymentPlanner.Describe( latest.Name )}, where the database is; roll back to that " +
						"release's predecessor with --to instead."
					);

				// plans only of later releases are not deployed: a rerun of a rollback that undid them
				if( target.Plans.All( plan => ReleaseOf( groups, plan ) > newest ) )
					return [ ];

				oldest = newest;

			break;
		}

		List<ReleaseGroup> releases = [ ];

		for( int index = newest; index >= oldest; index-- )
			releases.Add( groups[index] );

		return releases;
	}

	/// <summary>
	/// Plans the rollback. <paramref name="deployedPlans"/> holds, for every release
	/// <see cref="ReleasesToRollBack"/> names, the plans the journal holds for it (a set).
	/// </summary>
	public static RollbackForecast Plan(
		IReadOnlyList<ReleaseGroup> groups,
		JournaledRelease? latest,
		IReadOnlyDictionary<string, IReadOnlyCollection<string>> deployedPlans,
		RollbackTarget target
	)
	{
		ArgumentNullException.ThrowIfNull( deployedPlans );

		IReadOnlyList<ReleaseGroup> releases = ReleasesToRollBack( groups, latest, target );

		List<PlannedRollback> planned = [ ];

		foreach( ReleaseGroup release in releases )
		{
			if( !deployedPlans.TryGetValue( release.Name, out IReadOnlyCollection<string>? deployed ) )
				throw new ArgumentException(
					$"The journal's plans of {DeploymentPlanner.Describe( release.Name )} are needed to roll it back.",
					nameof(deployedPlans)
				);

			DeploymentPlanner.EnsureDeployedPlansAreInBundleRelease( latest!, release.Name, deployed, release, "roll back" );

			HashSet<string> held = new( deployed, StringComparer.Ordinal );

			List<DeployablePlan> plans = release.Plans.Where( plan => held.Contains( plan.Name ) ).ToList();

			if( target.Kind == RollbackTargetKind.Plans )
			{
				plans = SelectPlans( release, plans, target.Plans );

				// none of them deployed (any more): a rerun of a finished rollback
				if( plans.Count == 0 )
					return new RollbackForecast( latest, [ ] );
			}

			planned.Add( new PlannedRollback( release, plans ) );
		}

		return new RollbackForecast( latest, planned );
	}

	/// <summary>
	/// The requested plans among the deployed ones, refusing a deployed plan left behind that requires
	/// one of them.
	/// </summary>
	private static List<DeployablePlan> SelectPlans(
		ReleaseGroup release,
		List<DeployablePlan> deployed,
		IReadOnlyList<string> requested
	)
	{
		// names of later releases match nothing deployed here: they are not deployed
		HashSet<string> wanted = new( requested, StringComparer.Ordinal );

		List<DeployablePlan> selected = deployed.Where( plan => wanted.Contains( plan.Name ) ).ToList();
		HashSet<string> undone = new( selected.Select( plan => plan.Name ), StringComparer.Ordinal );

		List<string> stranded = deployed
			.Where( plan => !undone.Contains( plan.Name ) )
			.SelectMany( plan => plan.Dependencies
				.Where( undone.Contains )
				.Select( dependency => $"'{plan.Name}' requires '{dependency}'" )
			)
			.ToList();

		if( stranded.Count > 0 )
			throw new RollbackRefusedException(
				$"deployed plans of {DeploymentPlanner.Describe( release.Name )} require plans being rolled back " +
				$"({string.Join( ", ", stranded )}); roll them back too."
			);

		return selected;
	}

	private static string Quote( IEnumerable<string> names ) => string.Join( ", ", names.Select( name => $"'{name}'" ) );

	/// <summary>The index of the group holding <paramref name="plan"/>, or -1.</summary>
	private static int ReleaseOf( IReadOnlyList<ReleaseGroup> groups, string plan )
	{
		for( int index = 0; index < groups.Count; index++ )
			if( groups[index].Plans.Any( candidate => string.Equals( candidate.Name, plan, StringComparison.Ordinal ) ) )
				return index;

		return -1;
	}

	private static int IndexOf( IReadOnlyList<ReleaseGroup> groups, string release )
	{
		for( int index = 0; index < groups.Count; index++ )
			if( string.Equals( groups[index].Name, release, StringComparison.Ordinal ) )
				return index;

		return -1;
	}

	/// <summary>The release the groups continue from, outside them: the first group's dependency.</summary>
	private static bool IsContinuationBase( IReadOnlyList<ReleaseGroup> groups, string release )
		=> groups.Count > 0 && groups[0].HasDependency &&
			string.Equals( groups[0].Dependency, release, StringComparison.Ordinal );
}

/// <summary>
/// What a rollback undoes: <paramref name="Releases"/> newest first, in the order they are undone.
/// <paramref name="Journaled"/> is where the journal was (null: nothing journaled).
/// </summary>
public sealed record RollbackForecast( JournaledRelease? Journaled, IReadOnlyList<PlannedRollback> Releases )
{
	public bool NothingToRollBack => Releases.Count == 0;
}

/// <summary>
/// A release to undo and its deployed plans to undo, in <em>deployment</em> order — undoing runs them,
/// and each plan's steps, backwards. A release with no plans left still has its start undone in the
/// journal.
/// </summary>
public sealed record PlannedRollback( ReleaseGroup Group, IReadOnlyList<DeployablePlan> Plans );
