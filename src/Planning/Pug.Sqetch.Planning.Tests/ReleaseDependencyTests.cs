using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;
using Pug.Sqetch.Tests.Planning.Stores.FileSystem;

namespace Pug.Sqetch.Planning.Tests;

/// <summary>
/// A release holds its plans' dependencies, or follows the releases that do: creating a release
/// with plans, adding a plan to one and creating a plan in one all refuse a dependency that is
/// unreleased or in a later release. '--with-dependencies' brings unreleased ones along, and
/// refuses rather than moves one in a later release.
/// </summary>
public sealed class ReleaseDependencyTests : IDisposable
{
	private readonly TempProject _temp;
	private readonly FileSystemProjectStores _stores;
	private readonly IProject _project;

	public ReleaseDependencyTests()
	{
		_temp = TempProject.Create();
		_stores = _temp.Open();
		_project = ProjectFactory.Create(
			_stores.InfoStore, _stores.ScriptsStore,
			new ReleaseChainDependencyDeterminator( _stores.InfoStore ), TestData.User );
	}

	public void Dispose()
	{
		_project.Dispose();
		_stores.Dispose();
		_temp.Dispose();
	}

	private void Plan( string name, params string[] dependencies )
		=> _project.Add( new PlanDefinition( name, "", dependencies ) );

	private string? ReleaseOf( string plan )
	{
		string release = _stores.InfoStore.GetPlan( plan )!.Release;

		return release.Length == 0 ? null : release;
	}

	private static ReleaseDefinition Release( string name, string dependency = "" ) => new ( name, "", dependency );

	[Fact]
	public void CreatingAReleaseWithoutAnUnreleasedDependencyIsRefused()
	{
		Plan( "base" );
		Plan( "dependant", "base" );

		MissingPlanDependenciesException exception = Assert.Throws<MissingPlanDependenciesException>(
			() => _project.CreateRelease( Release( "1.0" ), ["dependant"] ) );

		(ProjectPlan missing, ICollection<string> dependants) = Assert.Single( exception.MissingDependencies ).Value;
		Assert.Equal( "base", missing.Definition.Name );
		Assert.Equal( ["dependant"], dependants );

		Assert.Null( _stores.InfoStore.GetRelease( "1.0" ) );
		Assert.Null( ReleaseOf( "dependant" ) );
	}

	[Fact]
	public void CreatingAReleaseWithItsDependenciesSucceeds()
	{
		Plan( "base" );
		Plan( "dependant", "base" );

		_project.CreateRelease( Release( "1.0" ), ["dependant", "base"] );

		Assert.Equal( "1.0", ReleaseOf( "base" ) );
		Assert.Equal( "1.0", ReleaseOf( "dependant" ) );
	}

	[Fact]
	public void ADependencyChainListedTogetherIsAccepted()
	{
		Plan( "c" );
		Plan( "b", "c" );
		Plan( "a", "b" );

		_project.CreateRelease( Release( "1.0" ), ["a", "b", "c"] );

		Assert.Equal( "1.0", ReleaseOf( "a" ) );
		Assert.Equal( "1.0", ReleaseOf( "c" ) );
	}

	[Theory]
	[InlineData( true )]
	[InlineData( false )]
	public void ADependencyInAnEarlierReleaseSatisfiesANewRelease( bool finalizeEarlier )
	{
		Plan( "base" );
		_project.CreateRelease( Release( "1.0" ), ["base"] );

		if( finalizeEarlier )
			_project.FinalizeRelease( "1.0" );

		Plan( "dependant", "base" );

		_project.CreateRelease( Release( "1.1", "1.0" ), ["dependant"] );

		Assert.Equal( "1.1", ReleaseOf( "dependant" ) );
		Assert.Equal( "1.0", ReleaseOf( "base" ) );
	}

	[Fact]
	public void AddingAPlanWithoutAnUnreleasedDependencyIsRefused()
	{
		Plan( "base" );
		Plan( "dependant", "base" );
		_project.CreateRelease( Release( "1.0" ) );

		Assert.Throws<MissingPlanDependenciesException>(
			() => _project.AddPlanToRelease( "1.0", "dependant", includeDependencies: false ) );

		Assert.Null( ReleaseOf( "dependant" ) );
		Assert.Null( ReleaseOf( "base" ) );
	}

	[Fact]
	public void AddingAPlanWhoseDependencyIsInTheSameReleaseSucceeds()
	{
		Plan( "base" );
		Plan( "dependant", "base" );
		_project.CreateRelease( Release( "1.0" ), ["base"] );

		_project.AddPlanToRelease( "1.0", "dependant", includeDependencies: false );

		Assert.Equal( "1.0", ReleaseOf( "dependant" ) );
	}

	[Theory]
	[InlineData( true )]
	[InlineData( false )]
	public void ADependencyInALaterReleaseIsRefusedAndNotMoved( bool includeDependencies )
	{
		Plan( "base" );
		Plan( "dependant", "base" );
		_project.CreateRelease( Release( "1.0" ) );
		_project.CreateRelease( Release( "1.1", "1.0" ), ["base"] );

		MissingPlanDependenciesException exception = Assert.Throws<MissingPlanDependenciesException>(
			() => _project.AddPlanToRelease( "1.0", "dependant", includeDependencies ) );

		Assert.Equal( ["base"], exception.MissingDependencies.Keys );
		Assert.Equal( "1.1", ReleaseOf( "base" ) );
		Assert.Null( ReleaseOf( "dependant" ) );
	}

	[Fact]
	public void WithDependenciesAddsEveryUnreleasedDependencyTransitively()
	{
		Plan( "c" );
		Plan( "b", "c" );
		Plan( "a", "b" );
		Plan( "unrelated" );
		_project.CreateRelease( Release( "1.0" ) );

		_project.AddPlanToRelease( "1.0", "a", includeDependencies: true );

		Assert.Equal( "1.0", ReleaseOf( "a" ) );
		Assert.Equal( "1.0", ReleaseOf( "b" ) );
		Assert.Equal( "1.0", ReleaseOf( "c" ) );
		Assert.Null( ReleaseOf( "unrelated" ) );
	}

	[Fact]
	public void WithDependenciesLeavesADependencyInAnEarlierReleaseWhereItIs()
	{
		Plan( "earlier" );
		_project.CreateRelease( Release( "1.0" ), ["earlier"] );
		Plan( "middle", "earlier" );
		Plan( "top", "middle" );
		_project.CreateRelease( Release( "1.1", "1.0" ) );

		_project.AddPlanToRelease( "1.1", "top", includeDependencies: true );

		Assert.Equal( "1.0", ReleaseOf( "earlier" ) );
		Assert.Equal( "1.1", ReleaseOf( "middle" ) );
		Assert.Equal( "1.1", ReleaseOf( "top" ) );
	}

	[Fact]
	public void CreatingAPlanInAReleaseAcceptsADependencyInAnEarlierRelease()
	{
		Plan( "base" );
		_project.CreateRelease( Release( "1.0" ), ["base"] );
		_project.CreateRelease( Release( "1.1", "1.0" ) );

		_project.Add( new PlanDefinition( "dependant", "", ["base"] ), "1.1" );

		Assert.NotNull( _stores.InfoStore.GetPlan( "dependant" ) );
	}

	[Fact]
	public void CreatingAPlanInAReleaseRefusesAnUnreleasedDependency()
	{
		Plan( "base" );
		_project.CreateRelease( Release( "1.0" ) );

		Assert.Throws<MissingPlanDependenciesException>(
			() => _project.Add( new PlanDefinition( "dependant", "", ["base"] ), "1.0" ) );

		Assert.Null( _stores.InfoStore.GetPlan( "dependant" ) );
	}

	[Fact]
	public void CreatingAPlanWithAnUnknownDependencyIsRefused()
	{
		_project.CreateRelease( Release( "1.0" ) );

		Assert.Throws<UnknownPlanException>(
			() => _project.Add( new PlanDefinition( "dependant", "", ["nowhere"] ), "1.0" ) );
		Assert.Throws<UnknownPlanException>(
			() => _project.Add( new PlanDefinition( "dependant", "", ["nowhere"] ) ) );
	}
}
