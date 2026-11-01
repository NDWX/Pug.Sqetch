using Pug.Sqetch.Deployment.Bundling;
using Pug.Sqetch.Deployment.Bundling.Layouts;
using Pug.Sqetch.Deployment;
using Pug.Sqetch.Deployment.Cli.Commands;
using static Pug.Sqetch.Tests.Deployment.Cli.Manifests;

namespace Pug.Sqetch.Tests.Deployment.Cli;

/// <summary>
/// How 'sqetch-deploy' turns a bundle into what the deployment engine takes: release groups out of
/// the manifest, and scripts out of the bundle through its layout.
/// </summary>
public class BundleReleaseGroupsTests
{
	[Fact]
	public void ReleasesKeepManifestOrderWithTheirPlansAndSteps()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" ), Release( "r2", "r1" )],
			[Plan( "alpha", "r1", "s1", "s2" ), Plan( "gamma", "r2", "s1" ), Plan( "beta", "r1", "s1" )] );

		IReadOnlyList<ReleaseGroup> groups = BundleReleaseGroups.From( manifest );

		Assert.Equal( ["r1", "r2"], groups.Select( group => group.Name ) );
		Assert.Equal( ["", "r1"], groups.Select( group => group.Dependency ) );
		Assert.Equal( ["alpha", "beta"], groups[0].Plans.Select( plan => plan.Name ) );
		Assert.Equal( ["s1", "s2"], groups[0].Plans[0].Steps.Select( step => step.Name ) );
		Assert.Equal( ["gamma"], groups[1].Plans.Select( plan => plan.Name ) );
	}

	[Fact]
	public void UnreleasedPlansFormATrailingPseudoReleaseFollowingTheLastRelease()
	{
		BundleManifest manifest = Manifest(
			[Release( "r1" )],
			[Plan( "y", "", "s1" ), Plan( "a", "r1", "s1" ), Plan( "z", "", "s1" )] );

		IReadOnlyList<ReleaseGroup> groups = BundleReleaseGroups.From( manifest );

		ReleaseGroup pseudo = groups[^1];

		Assert.Equal( 2, groups.Count );
		Assert.Equal( ("", "r1"), (pseudo.Name, pseudo.Dependency) );
		Assert.Equal( ["y", "z"], pseudo.Plans.Select( plan => plan.Name ) );
	}

	[Fact]
	public void UnreleasedPlansAloneStartTheLineage()
	{
		IReadOnlyList<ReleaseGroup> groups = BundleReleaseGroups.From( Manifest( [], [Plan( "z", "", "s1" )] ) );

		ReleaseGroup pseudo = Assert.Single( groups );

		Assert.False( pseudo.HasDependency );
	}

	[Fact]
	public void ReleasesThatDoNotFormAContiguousChainAreRefused()
	{
		BundleManifest manifest = Manifest(
			[Release( "r0" ), Release( "r2", "r1" )],
			[Plan( "a", "r0", "s1" ), Plan( "c", "r2", "s1" )] );

		InvalidBundleException error = Assert.Throws<InvalidBundleException>( () => BundleReleaseGroups.From( manifest ) );

		Assert.Equal(
			"Release 'r2' does not depend on 'r0', the release preceding it in the bundle.",
			error.Message );
	}

	[Fact]
	public void AReleaseListedTwiceIsRefused()
	{
		BundleManifest manifest = Manifest( [Release( "r1" ), Release( "r1", "r1" )], [Plan( "a", "r1", "s1" )] );

		InvalidBundleException error = Assert.Throws<InvalidBundleException>( () => BundleReleaseGroups.From( manifest ) );

		Assert.Equal( "Bundle manifest lists release 'r1' more than once.", error.Message );
	}

	[Fact]
	public void PlanReferencingAnUnlistedReleaseIsRefused()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r9", "s1" )] );

		InvalidBundleException error = Assert.Throws<InvalidBundleException>( () => BundleReleaseGroups.From( manifest ) );

		Assert.Contains( "'r9'", error.Message );
	}

	[Fact]
	public void ScriptsAreReadFromTheBundleAtTheLayoutsPaths()
	{
		DefaultBundleLayout layout = new ();
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" )] );

		BundleScriptContentReader scripts = new ( ReaderFor( manifest, layout ), layout );

		Assert.Equal( "-- deploy a/s1", scripts.ReadDeployScript( "r1", "a", "s1" ) );
		Assert.Equal( "-- rollback a/s1", scripts.ReadRollbackScript( "r1", "a", "s1" ) );
	}

	[Fact]
	public void AScriptMissingFromTheBundleIsAnError()
	{
		DefaultBundleLayout layout = new ();
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" )] );

		BundleScriptContentReader scripts = new ( ReaderFor( manifest, layout ), layout );

		Assert.Throws<BundlingException>( () => scripts.ReadDeployScript( "r1", "b", "s1" ) );
	}
}
