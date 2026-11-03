namespace Pug.Sqetch.Deployment;

/// <summary>
/// The resume decision: given the release groups to deploy and what the journal says, which groups
/// deploy, which of their plans are skipped, and what is reported along the way. Pure — no database,
/// no listener — so <see cref="DeploymentEngine"/> deploying and <see cref="DeploymentForecaster"/>
/// forecasting decide the same way from one implementation.
///
/// Where deployment starts follows from the journal alone: nothing journaled deploys the groups
/// whole, an <em>incompletely</em> deployed release resumes inside itself (so the groups must contain
/// it), skipping the plans the journal already holds, and a <em>completely</em> deployed release hands
/// over to whichever group depends on it — which may be a release the groups do not contain, making
/// this a continuation. Groups that do not fit the journal are refused with
/// <see cref="IncompatibleBundleException"/>.
/// </summary>
public static class DeploymentPlanner
{
	/// <summary>
	/// Plans a deployment of <paramref name="groups"/> against a database whose journal is at
	/// <paramref name="latest"/> (null: nothing journaled). <paramref name="deployedPlans"/> are the
	/// plans of <paramref name="latest"/> the journal holds, needed only when that release is
	/// incomplete; they are a set — resume is by membership, never by position, because a release's
	/// plans are only partly ordered.
	/// </summary>
	public static DeploymentForecast Plan(
		IReadOnlyList<ReleaseGroup> groups,
		JournaledRelease? latest,
		IReadOnlyCollection<string>? deployedPlans
	)
	{
		ArgumentNullException.ThrowIfNull( groups );

		if( latest is { Completed: false } && deployedPlans is null )
			throw new ArgumentException(
				"The plans the journal holds for an incompletely deployed release are needed to resume it.",
				nameof(deployedPlans)
			);

		List<ForecastNote> notes = [ ];
		List<PlannedRelease> releases = [ ];
		IReadOnlySet<string> none = new HashSet<string>( StringComparer.Ordinal );

		bool firstGroupFound = false,
			pseudoReleaseFound = false;

		ReleaseGroup? lastGroup = null;

		foreach( ReleaseGroup group in groups )
		{
			if( pseudoReleaseFound || latest is { Name.Length: 0, Completed: true } )
				throw new IncompatibleBundleException( latest, "A pseudo bundle cannot be followed by other releases" );

			lastGroup = group;
			pseudoReleaseFound = group.Name.Length == 0;

			// subsequent releases after the first to-be-included release are deployed
			if( firstGroupFound )
			{
				releases.Add( new PlannedRelease( group, none, Resumed: false ) );

				continue;
			}

			// the database has no previous deployment: the groups must start the lineage, since
			// the first release of a lineage has no dependency
			if( latest is null )
			{
				if( group.HasDependency )
					throw new IncompatibleBundleException(
						null,
						$"this bundle continues from {Describe( groups[0].Dependency )} " +
						"but the database has no deployed releases."
					);

				releases.Add( new PlannedRelease( group, none, Resumed: false ) );
				firstGroupFound = true;

				continue;
			}

			// the journaled release itself: skipped when complete, resumed otherwise
			if( string.Equals( latest.Name, group.Name, StringComparison.Ordinal ) )
			{
				firstGroupFound = true;

				if( latest.Completed )
				{
					notes.Add( new ForecastNote( ForecastNoteKind.SkippingRelease, latest.Name ) );

					continue;
				}

				// a journaled plan the bundle's copy of the release lacks means the release changed
				// since, and the database should be restored from a clean backup
				EnsureDeployedPlansAreInBundleRelease( latest, deployedPlans!, group );

				releases.Add(
					new PlannedRelease( group, new HashSet<string>( deployedPlans!, StringComparer.Ordinal ), Resumed: true )
				);

				continue;
			}

			// the release depending on the journaled one: a continuation
			if( string.Equals( latest.Name, group.Dependency, StringComparison.Ordinal ) )
			{
				// nothing may follow the unreleased-plans pseudo-release, which only test bundles
				// deploy; this usually means the project's release structure changed, and the
				// database should be restored from a clean backup
				if( latest.Name.Length == 0 )
				{
					notes.Add( new ForecastNote( ForecastNoteKind.SkippingRelease, group.Name ) );

					continue;
				}

				notes.Add( new ForecastNote( ForecastNoteKind.ContinuingFrom, latest.Name ) );
				releases.Add( new PlannedRelease( group, none, Resumed: false ) );
				firstGroupFound = true;

				continue;
			}

			// releases before the journaled one are skipped
			notes.Add( new ForecastNote( ForecastNoteKind.SkippingRelease, group.Name ) );
		}

		// nothing new to deploy is fine only when the bundle ends at the journaled release
		if( releases.Count == 0 &&
			latest is { Name.Length: > 0 } &&
			!string.Equals( lastGroup?.Name, latest.Name, StringComparison.Ordinal ) )
			throw new IncompatibleBundleException(
				latest,
				$"database is at {Describe( latest.Name )}, which this bundle neither contains nor continues from."
			);

		return new DeploymentForecast( latest, notes, releases );
	}

	/// <summary>
	/// The resume and hand-over decisions only look at the first group's dependency and match the
	/// journal by name, so groups that repeat a name or skip a link would be deployed out of lineage
	/// rather than refused.
	/// </summary>
	public static void EnsureLineage( IReadOnlyList<ReleaseGroup> groups )
	{
		ArgumentNullException.ThrowIfNull( groups );

		HashSet<string> names = new( StringComparer.Ordinal );

		for( int index = 0; index < groups.Count; index++ )
		{
			ReleaseGroup group = groups[index];

			if( !names.Add( group.Name ) )
				throw new ArgumentException( $"Release '{group.Name}' is listed more than once.", nameof(groups) );

			if( index > 0 && !string.Equals( group.Dependency, groups[index - 1].Name, StringComparison.Ordinal ) )
				throw new ArgumentException(
					$"Release '{group.Name}' does not depend on '{groups[index - 1].Name}', the release preceding it.",
					nameof(groups)
				);
		}
	}

	/// <summary>
	/// A partially deployed release must finish before any later release, so groups that do not
	/// contain it cannot be deployed at all. Checked before its plans are queried, since groups that
	/// cannot be used are not worth the round trip.
	/// </summary>
	public static void EnsureResumable( JournaledRelease latest, IReadOnlyList<ReleaseGroup> groups )
	{
		ArgumentNullException.ThrowIfNull( latest );
		ArgumentNullException.ThrowIfNull( groups );

		if( latest.Completed ||
			groups.Any( group => string.Equals( group.Name, latest.Name, StringComparison.Ordinal ) ) )
			return;

		throw new IncompatibleBundleException(
			latest,
			$"{Describe( latest.Name )} is only partially deployed; deploy a bundle that contains it " +
			$"so its remaining plans deploy{( groups.Count > 0 ? $" before {Describe( groups[0].Name )}" : "" )}."
		);
	}

	/// <summary>Renders a release name for a message; mirrors the CLI listener's wording.</summary>
	internal static string Describe( string release )
		=> release.Length == 0 ? "the unreleased-plans pseudo-release" : $"release '{release}'";

	private static void EnsureDeployedPlansAreInBundleRelease(
		JournaledRelease journaledRelease,
		IReadOnlyCollection<string> deployedPlans,
		ReleaseGroup bundlePlanGroup
	)
	{
		List<string> missingDeployedPlans =
			deployedPlans.Except( bundlePlanGroup.Plans.Select( plan => plan.Name ) ).ToList();

		if( missingDeployedPlans.Count > 0 )
			throw new IncompatibleBundleException(
				journaledRelease,
				$"the database has deployed plans of {Describe( journaledRelease.Name )} that this bundle does " +
				$"not contain: {string.Join( ", ", missingDeployedPlans.Select( plan => $"'{plan}'" ) )}; deploy a " +
				"bundle that contains them, or restore the database."
			);
	}
}

/// <summary>
/// What a deployment of some release groups would do: the notes it reports first, in order, and the
/// releases it deploys. <paramref name="Journaled"/> is where the journal was (null: nothing
/// journaled). An empty <paramref name="Releases"/> is nothing to deploy.
/// </summary>
public sealed record DeploymentForecast(
	JournaledRelease? Journaled,
	IReadOnlyList<ForecastNote> Notes,
	IReadOnlyList<PlannedRelease> Releases
)
{
	public bool NothingToDeploy => Releases.Count == 0;

	/// <summary>The release the deployment continues from, when it continues a lineage rather than resuming in it.</summary>
	public string? ContinuingFrom
		=> Notes.FirstOrDefault( note => note.Kind == ForecastNoteKind.ContinuingFrom )?.Release;
}

/// <summary>
/// A release to deploy. <paramref name="SkippedPlans"/> are the plans the journal already holds,
/// empty unless the release is <paramref name="Resumed"/> — partly deployed by an earlier run, whose
/// start is therefore journaled already.
/// </summary>
public sealed record PlannedRelease( ReleaseGroup Group, IReadOnlySet<string> SkippedPlans, bool Resumed )
{
	/// <summary>The plans that would deploy, in deployment order.</summary>
	public IEnumerable<DeployablePlan> PlansToDeploy => Group.Plans.Where( plan => !SkippedPlans.Contains( plan.Name ) );
}

/// <summary>Something a deployment reports before deploying anything: a release it skips, or the one it continues from.</summary>
public sealed record ForecastNote( ForecastNoteKind Kind, string Release );

public enum ForecastNoteKind
{
	/// <summary>The release is already deployed, or precedes the one the journal is at.</summary>
	SkippingRelease,

	/// <summary>
	/// The groups do not contain the journaled release but continue from it: nothing is skipped in
	/// that case, so this is the only indication of what the deployment builds on.
	/// </summary>
	ContinuingFrom
}
