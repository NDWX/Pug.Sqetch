namespace Pug.Sqetch.Bundling;

/// <summary>
/// The layout-neutral manifest of a bundle being read. Layouts translate their on-disk
/// manifest schema to and from this model. All lists are in deployment-chronological
/// order; that order is part of the bundle contract.
/// </summary>
public sealed record BundleManifest(
	BundleManifestProject Project,
	BundleSelection Selection,
	IReadOnlyList<BundleManifestRelease> Releases,
	IReadOnlyList<BundleManifestPlan> Plans );

public sealed record BundleManifestProject( string Name, string Description, string Engine );

/// <summary>
/// <paramref name="Dependency"/> names the release this one follows in the lineage, empty for
/// the release that starts it. A manifest that omits the member falls back to the preceding
/// release in the list, so a bundle's releases always form a contiguous chain.
/// </summary>
public sealed record BundleManifestRelease( string Name, string Description, string Dependency, bool Finalized );

/// <summary><paramref name="Release"/> is empty for unreleased plans (test selection).</summary>
public sealed record BundleManifestPlan(
	string Name,
	string Description,
	string Release,
	IReadOnlyList<string> Dependencies,
	IReadOnlyList<BundleManifestStep> Steps );

public sealed record BundleManifestStep(
	string Name,
	string Description,
	IReadOnlyList<string> Dependencies );
