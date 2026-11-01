using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Deployment.Bundling;

public enum BundleSelection
{
	/// <summary>Plans of finalized releases only.</summary>
	Finalized,

	/// <summary>Every plan, regardless of release status.</summary>
	Test
}

/// <summary>
/// Assembled bundle content. All lists are in deployment-chronological order; that order is
/// part of the bundle contract.
/// <paramref name="Journaling"/> is the project's journaling SQL, carried with the bundle so a
/// deployment journals through the maintainer's own statements rather than a driver-specific
/// writer. Its type cannot be constructed incomplete, so a bundle always has all fifteen slots.
/// </summary>
public sealed record Bundle(
	ProjectDefinition Project,
	BundleSelection Selection,
	JournalingStatements Journaling,
	IReadOnlyList<BundleRelease> Releases,
	IReadOnlyList<BundlePlan> Plans );

/// <summary>
/// <paramref name="Dependency"/> names the release this one follows in the project's single
/// release lineage; it is empty for the release that starts the lineage. A bundle that omits
/// the dependency's own release continues from it, and deployment accepts that only when the
/// database has that release completely deployed.
/// </summary>
public sealed record BundleRelease( string Name, string Description, string Dependency, bool Finalized );

/// <summary><paramref name="Release"/> is empty for unreleased plans (test selection).</summary>
public sealed record BundlePlan(
	string Name,
	string Description,
	string Release,
	IReadOnlyList<string> Dependencies,
	IReadOnlyList<BundleStep> Steps );

/// <summary>
/// <paramref name="OpenScripts"/> opens the step's scripts on demand — lazy so that at most
/// one step's streams are open at a time — and is valid only while the project the bundle
/// was assembled from is alive. The caller disposes the result; a script deleted after
/// assembly surfaces as <see cref="MissingStepScriptsException"/>.
/// </summary>
public sealed record BundleStep(
	string Name,
	string Description,
	IReadOnlyList<string> Dependencies,
	Func<StepScripts> OpenScripts );
