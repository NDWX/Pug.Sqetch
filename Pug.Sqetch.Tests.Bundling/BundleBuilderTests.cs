using Pug.Sqetch.Bundling;
using Pug.Sqetch.Models;
using Pug.Sqetch.ProjectInfoStores.FileSystem;
using Pug.Sqetch.Tests.Stores.FileSystem;

namespace Pug.Sqetch.Tests.Bundling;

/// <summary>
/// Drives <see cref="BundleBuilder"/> against the real engine over a temp file-system
/// project: two chained releases (first finalized), plus an unreleased plan.
/// </summary>
public class BundleBuilderTests
{
	[Fact]
	public void FinalizedSelectionBundlesOnlyFinalizedReleasesInChainOrder()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = CreateProject( stores );

		PopulateProject( temp, project );

		Bundle bundle = Builder( project ).Assemble( BundleSelection.Finalized );

		Assert.Equal( BundleSelection.Finalized, bundle.Selection );
		Assert.Equal( "test-project", bundle.Project.Name );
		Assert.Equal( ["2026.07"], bundle.Releases.Select( x => x.Name ).ToArray() );
		Assert.True( bundle.Releases[0].Finalized );
		Assert.Equal( "", bundle.Releases[0].Dependency );

		// dependency order within the release: 'index' depends on 'table'
		Assert.Equal( ["table", "index"], bundle.Plans.Select( x => x.Name ).ToArray() );
		Assert.Equal( ["create"], bundle.Plans[0].Steps.Select( x => x.Name ).ToArray() );

		using StepScripts scripts = bundle.Plans[0].Steps[0].OpenScripts();
		Assert.Equal( "-- deploy create", new StreamReader( scripts.DeployScript! ).ReadToEnd() );
	}

	[Fact]
	public void TestSelectionAppendsOpenReleasesThenUnreleasedPlans()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = CreateProject( stores );

		PopulateProject( temp, project );

		Bundle bundle = Builder( project ).Assemble( BundleSelection.Test );

		Assert.Equal( ["2026.07", "2026.08"], bundle.Releases.Select( x => x.Name ).ToArray() );
		Assert.Equal( [true, false], bundle.Releases.Select( x => x.Finalized ).ToArray() );
		Assert.Equal( ["", "2026.07"], bundle.Releases.Select( x => x.Dependency ).ToArray() );

		Assert.Equal( ["table", "index", "cleanup", "pending"], bundle.Plans.Select( x => x.Name ).ToArray() );
		Assert.Equal( ["2026.07", "2026.07", "2026.08", ""], bundle.Plans.Select( x => x.Release ).ToArray() );
	}

	[Fact]
	public void AssembleSinceYieldsOnlyReleasesAfterIt()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = CreateProject( stores );

		PopulateChainedFinalizedReleases( temp, project );

		Bundle bundle = Builder( project ).Assemble( BundleSelection.Finalized, since: "2026.07" );

		Assert.Equal( ["2026.08"], bundle.Releases.Select( x => x.Name ).ToArray() );
		Assert.Equal( "2026.07", bundle.Releases[0].Dependency );
		Assert.Equal( ["cleanup"], bundle.Plans.Select( x => x.Name ).ToArray() );
	}

	[Fact]
	public void AssembleSinceAnOpenReleaseIsDiagnosableNotReportedAsUnknown()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = CreateProject( stores );

		PopulateProject( temp, project );

		// '2026.08' is open (unfinalized); a Finalized selection still locates it in the
		// full chain rather than reporting it unknown - it just leaves no finalized
		// releases after it, which is an empty bundle
		Assert.Throws<EmptyBundleException>(
			() => Builder( project ).Assemble( BundleSelection.Finalized, since: "2026.08" ) );
	}

	[Fact]
	public void AssembleThrowsUnknownReleaseExceptionWhenSinceIsNotInTheChain()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = CreateProject( stores );

		PopulateChainedFinalizedReleases( temp, project );

		UnknownReleaseException error = Assert.Throws<UnknownReleaseException>(
			() => Builder( project ).Assemble( BundleSelection.Finalized, since: "nope" ) );

		Assert.Equal( "nope", error.ReleaseName );
	}

	[Fact]
	public void AssembleSinceTheLastReleaseThrowsEmptyBundleException()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = CreateProject( stores );

		PopulateChainedFinalizedReleases( temp, project );

		Assert.Throws<EmptyBundleException>(
			() => Builder( project ).Assemble( BundleSelection.Finalized, since: "2026.08" ) );
	}

	[Fact]
	public void AssembleThrowsWhenNothingMatches()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = CreateProject( stores );

		Assert.Throws<EmptyBundleException>( () => Builder( project ).Assemble( BundleSelection.Finalized ) );
		Assert.Throws<EmptyBundleException>( () => Builder( project ).Assemble( BundleSelection.Test ) );
	}

	[Fact]
	public void AssembleThrowsWhenAStepScriptIsMissing()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = CreateProject( stores );

		PopulateProject( temp, project );

		File.Delete( Path.Combine(
			temp.Root, "releases", "2026.07", "plans", "table", "steps", "create", "verify.sql" ) );

		MissingStepScriptsException missing = Assert.Throws<MissingStepScriptsException>(
			() => Builder( project ).Assemble( BundleSelection.Finalized ) );

		Assert.Equal( "table", missing.Plan );
		Assert.Equal( ["verify"], missing.Scripts );
	}

	/// <summary>
	/// A bundle journals through the project's own SQL, so one assembled without it could never be
	/// deployed. Refused at assembly, where the maintainer can still fix it, rather than at deploy
	/// time, where the operator cannot.
	/// </summary>
	[Fact]
	public void AssembleNamesEveryJournalingStatementTheProjectHasNotSet()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = CreateProject( stores );

		PopulateProject( temp, project );

		// unset two of the fifteen by writing blanks the store reads back as unset
		project.SetJournalingStatement( JournalingSlot.PrepareJournal, "-- kept" );

		File.Delete( Path.Combine( temp.Root, "journaling", "OnPlanDeployed.sql" ) );
		File.Delete( Path.Combine( temp.Root, "journaling", "GetLatestRelease.sql" ) );

		MissingJournalingStatementsException missing = Assert.Throws<MissingJournalingStatementsException>(
			() => Builder( project ).Assemble( BundleSelection.Finalized ) );

		Assert.Equal(
			[JournalingSlot.PlanDeployed, JournalingSlot.GetLatestRelease], missing.Slots );
	}

	private static IProject CreateProject( FileSystemProjectStores stores )
		=> ProjectFactory.Create(
			stores.InfoStore, stores.ScriptsStore,
			new ReleaseChainDependencyDeterminator( stores.InfoStore ), TestData.User );

	private static BundleBuilder Builder( IProject project )
		=> new ( project );

	/// <summary>
	/// 'table' ← 'index' in finalized release 2026.07, 'cleanup' in open release 2026.08,
	/// 'pending' unreleased; every step gets its three scripts seeded.
	/// </summary>
	private static void PopulateProject( TempProject temp, IProject project )
	{
		project.Add( new PlanDefinition( "table", "Create table", [] ) );
		project.Add( new PlanDefinition( "index", "Create index", ["table"] ) );
		project.Add( new PlanDefinition( "cleanup", "Cleanup", [] ) );
		project.Add( new PlanDefinition( "pending", "Not yet released", [] ) );

		SeedScripts( temp, project.Add( new StepDefinition( "table", "create", "", [] ), "table" ), "create" );
		SeedScripts( temp, project.Add( new StepDefinition( "index", "add", "", [] ), "index" ), "add" );
		SeedScripts( temp, project.Add( new StepDefinition( "cleanup", "drop", "", [] ), "cleanup" ), "drop" );
		SeedScripts( temp, project.Add( new StepDefinition( "pending", "later", "", [] ), "pending" ), "later" );

		SeedJournaling( project );

		project.CreateRelease( new ReleaseDefinition( "2026.07", "July", "" ), ["table", "index"] );
		project.FinalizeRelease( "2026.07" );
		project.CreateRelease( new ReleaseDefinition( "2026.08", "August", "2026.07" ), ["cleanup"] );
	}

	/// <summary>'table' in finalized release 2026.07, 'cleanup' in finalized release 2026.08.</summary>
	private static void PopulateChainedFinalizedReleases( TempProject temp, IProject project )
	{
		project.Add( new PlanDefinition( "table", "Create table", [] ) );
		project.Add( new PlanDefinition( "cleanup", "Cleanup", [] ) );

		SeedScripts( temp, project.Add( new StepDefinition( "table", "create", "", [] ), "table" ), "create" );
		SeedScripts( temp, project.Add( new StepDefinition( "cleanup", "drop", "", [] ), "cleanup" ), "drop" );

		SeedJournaling( project );

		project.CreateRelease( new ReleaseDefinition( "2026.07", "July", "" ), ["table"] );
		project.FinalizeRelease( "2026.07" );
		project.CreateRelease( new ReleaseDefinition( "2026.08", "August", "2026.07" ), ["cleanup"] );
		project.FinalizeRelease( "2026.08" );
	}

	/// <summary>
	/// Every journaling slot, so <see cref="BundleBuilder.Assemble"/>'s completeness check passes.
	/// Bundling a project that has not set them is covered separately.
	/// </summary>
	private static void SeedJournaling( IProject project )
	{
		foreach( JournalingSlot slot in JournalingSlots.All )
			project.SetJournalingStatement( slot, $"-- {slot}" );
	}

	private static void SeedScripts( TempProject temp, StepScriptKeys keys, string step )
	{
		foreach( (string key, string kind) in new[]
				{
					(keys.DeployScript, "deploy"), (keys.VerifyScript, "verify"), (keys.RollbackScript, "rollback")
				} )
			File.WriteAllText(
				Path.Combine( temp.Root, key.Replace( '/', Path.DirectorySeparatorChar ) ),
				$"-- {kind} {step}" );
	}
}
