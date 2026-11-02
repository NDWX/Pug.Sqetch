using Pug.Sqetch.Deployment.Bundling;

namespace Pug.Sqetch.Deployment.Cli.Commands;

/// <summary>
/// Determines the release groups a bundle deploys, from its manifest. The deployment engine takes
/// groups rather than a manifest, so this is where a bundle's notion of releases and plans becomes
/// the engine's.
/// </summary>
public static class BundleReleaseGroups
{
	/// <summary>
	/// Releases in manifest order, each with its plans in manifest order; unreleased plans (empty
	/// release name) form a trailing pseudo-release named "" which depends on the last real release in
	/// the bundle, so it is a link in the chain like any other and a completed release hands over to
	/// it. The real releases must form a contiguous chain — each names the one preceding it in the
	/// bundle — and every plan must belong to a listed release; otherwise the manifest is refused with
	/// an <see cref="InvalidBundleException"/>.
	/// </summary>
	public static IReadOnlyList<ReleaseGroup> From( BundleManifest manifest )
	{
		ArgumentNullException.ThrowIfNull( manifest );

		List<(BundleManifestRelease Release, List<DeployablePlan> Plans)> releases =
			manifest.Releases.Select( release => ( release, new List<DeployablePlan>() ) ).ToList();

		Dictionary<string, List<DeployablePlan>> byName = new( StringComparer.Ordinal );

		foreach( ( BundleManifestRelease release, List<DeployablePlan> plans ) in releases )
			if( !byName.TryAdd( release.Name, plans ) )
				throw new InvalidBundleException(
					$"Bundle manifest lists release '{release.Name}' more than once."
				);

		for( int index = 1; index < releases.Count; index++ )
			if( !string.Equals(
					releases[index].Release.Dependency,
					releases[index - 1].Release.Name,
					StringComparison.Ordinal
				) )
				throw new InvalidBundleException(
					$"Release '{releases[index].Release.Name}' does not depend on '{releases[index - 1].Release.Name}', " +
					"the release preceding it in the bundle."
				);

		List<DeployablePlan>? unreleased = null;

		foreach( BundleManifestPlan plan in manifest.Plans )
			if( plan.Release.Length == 0 )
				( unreleased ??= [ ] ).Add( Plan( plan ) );
			else if( byName.TryGetValue( plan.Release, out List<DeployablePlan>? plans ) )
				plans.Add( Plan( plan ) );
			else
				throw new InvalidBundleException(
					$"Plan '{plan.Name}' references release '{plan.Release}' which the manifest does not list."
				);

		List<ReleaseGroup> groups = releases
			.Select( entry => new ReleaseGroup(
					entry.Release.Name,
					entry.Release.Description,
					entry.Release.Dependency,
					entry.Plans
				)
			)
			.ToList();

		// empty when the bundle lists no release: then the unreleased plans start the lineage
		if( unreleased is not null )
			groups.Add( new ReleaseGroup( "", "", groups.Count > 0 ? groups[^1].Name : "", unreleased ) );

		return groups;
	}

	private static DeployablePlan Plan( BundleManifestPlan plan )
		=> new(
			plan.Name,
			plan.Description,
			plan.Steps.Select( step => new DeployableStep( step.Name, step.Description ) ).ToList()
		);
}