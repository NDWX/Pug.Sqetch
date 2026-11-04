using Pug.Sqetch.Planning;
using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

namespace Pug.Sqetch.Tests.Planning.Stores.FileSystem;

/// <summary>
/// <see cref="FileSystemProjectInfoStore.SetPlanRelease"/> moves a plan's folder in one rename,
/// straight from where it is, so a move cut short never leaves the plan unreleased.
/// </summary>
public sealed class SetPlanReleaseTests : IDisposable
{
	private readonly TempProject _project;
	private readonly FileSystemProjectStores _stores;

	public SetPlanReleaseTests()
	{
		_project = TempProject.Create();
		_stores = _project.Open();

		_stores.InfoStore.AddPlan( new ObjectDefinition( "movable", "" ), [ ], TestData.Context() );
		_stores.InfoStore.AddRelease( new ReleaseDefinition( "1.0", "", "" ), TestData.Context() );
		_stores.InfoStore.AddRelease( new ReleaseDefinition( "1.1", "", "1.0" ), TestData.Context() );
	}

	public void Dispose()
	{
		_stores.Dispose();
		_project.Dispose();
	}

	private string PlanDirectory( string release )
		=> release.Length == 0 ?
			Path.Combine( _project.Root, "plans", "movable" ) :
			Path.Combine( _project.Root, "releases", release, "plans", "movable" );

	private void AssertPlanIsIn( string release )
	{
		foreach( string other in new[] { "", "1.0", "1.1" }.Where( x => x != release ) )
			Assert.False( Directory.Exists( PlanDirectory( other ) ), $"plan folder left under '{other}'" );

		Assert.True( File.Exists( Path.Combine( PlanDirectory( release ), "plan.json" ) ) );
		Assert.Equal( release, _stores.InfoStore.GetPlan( "movable" )!.Release );

		// a fresh open reads the index from disk, not this session's copy
		using FileSystemProjectStores reopened = _project.Open();

		Assert.Equal( release, reopened.InfoStore.GetPlan( "movable" )!.Release );
	}

	[Fact]
	public void APlanMovesFromOneReleaseToAnother()
	{
		_stores.InfoStore.AddReleasePlan( "1.0", "movable", TestData.Context() );

		_stores.InfoStore.SetPlanRelease( "movable", "1.1", TestData.Context() );

		AssertPlanIsIn( "1.1" );

		Assert.Contains(
			_stores.InfoStore.GetReleasePlans( "1.1" ),
			x => x.Definition.Name == "movable"
		);

		Assert.DoesNotContain(
			_stores.InfoStore.GetReleasePlans( "1.0" ),
			x => x.Definition.Name == "movable"
		);
	}

	[Fact]
	public void AnUnreleasedPlanMovesStraightIntoTheRelease()
	{
		_stores.InfoStore.SetPlanRelease( "movable", "1.0", TestData.Context() );

		AssertPlanIsIn( "1.0" );
	}

	[Fact]
	public void APlanAlreadyInTheReleaseIsLeftAsItIs()
	{
		_stores.InfoStore.AddReleasePlan( "1.0", "movable", TestData.Context() );

		string planFile = Path.Combine( PlanDirectory( "1.0" ), "plan.json" );
		string before = File.ReadAllText( planFile );

		_stores.InfoStore.SetPlanRelease( "movable", "1.0", TestData.Context() );

		Assert.Equal( before, File.ReadAllText( planFile ) );
		AssertPlanIsIn( "1.0" );
	}

	[Fact]
	public void AnUnknownPlanOrReleaseOrAnEmptyReleaseIsRefused()
	{
		_stores.InfoStore.AddReleasePlan( "1.0", "movable", TestData.Context() );

		Assert.Throws<UnknownPlanException>( () =>
			_stores.InfoStore.SetPlanRelease( "missing", "1.1", TestData.Context() )
		);

		Assert.Throws<UnknownReleaseException>( () =>
			_stores.InfoStore.SetPlanRelease( "movable", "missing", TestData.Context() )
		);

		Assert.Throws<ArgumentException>( () =>
			_stores.InfoStore.SetPlanRelease( "movable", "", TestData.Context() )
		);

		AssertPlanIsIn( "1.0" );
	}

	[Fact]
	public void AMoveIntoAFinalizedReleaseIsRefused()
	{
		_stores.InfoStore.AddPlan( new ObjectDefinition( "frozen", "" ), [ ], TestData.Context() );
		_stores.InfoStore.AddReleasePlan( "1.0", "frozen", TestData.Context() );
		_stores.InfoStore.SetReleaseContext( "1.0", TestData.Context() );
		_stores.InfoStore.AddReleasePlan( "1.1", "movable", TestData.Context() );

		Assert.Throws<ReleaseFinalizedException>( () =>
			_stores.InfoStore.SetPlanRelease( "movable", "1.0", TestData.Context() )
		);

		AssertPlanIsIn( "1.1" );
	}

	[Fact]
	public void AMoveOutOfAFinalizedReleaseIsRefused()
	{
		_stores.InfoStore.AddReleasePlan( "1.0", "movable", TestData.Context() );
		_stores.InfoStore.SetReleaseContext( "1.0", TestData.Context() );

		Assert.Throws<ReleaseFinalizedException>( () =>
			_stores.InfoStore.SetPlanRelease( "movable", "1.1", TestData.Context() )
		);

		AssertPlanIsIn( "1.0" );
	}
}
