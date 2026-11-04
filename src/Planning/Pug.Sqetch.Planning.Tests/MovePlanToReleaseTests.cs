using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;
using Pug.Sqetch.Tests.Planning.Stores.FileSystem;

namespace Pug.Sqetch.Planning.Tests;

/// <summary>
/// <see cref="IProject.MovePlanToRelease"/> checks the plan's dependencies and dependants before
/// changing anything, so a refused move leaves the plan in the release it was in.
/// </summary>
public sealed class MovePlanToReleaseTests : IDisposable
{
	private readonly TempProject _temp;
	private readonly FileSystemProjectStores _stores;
	private readonly IProject _project;

	public MovePlanToReleaseTests()
	{
		_temp = TempProject.Create();
		_stores = _temp.Open();

		_project = ProjectFactory.Create(
			_stores.InfoStore,
			_stores.ScriptsStore,
			new ReleaseChainDependencyDeterminator( _stores.InfoStore ),
			TestData.User
		);
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

	private static ReleaseDefinition Release( string name, string dependency = "" ) => new( name, "", dependency );

	private void TwoReleases( IEnumerable<string>? first = null, IEnumerable<string>? second = null )
	{
		_project.CreateRelease( Release( "1.0" ), first ?? [ ] );
		_project.CreateRelease( Release( "1.1", "1.0" ), second ?? [ ] );
	}

	[Fact]
	public void AMoveToALaterReleaseMovesThePlan()
	{
		Plan( "movable" );
		TwoReleases( [ "movable" ] );

		_project.MovePlanToRelease( "movable", "1.1", includeDependencies: false );

		Assert.Equal( "1.1", ReleaseOf( "movable" ) );
		Assert.DoesNotContain( _stores.InfoStore.GetReleasePlans( "1.0" ), x => x.Definition.Name == "movable" );
	}

	[Fact]
	public void AMoveRefusedForAMissingDependencyLeavesThePlanInItsRelease()
	{
		Plan( "base" );
		Plan( "dependant", "base" );
		TwoReleases( second: [ "base", "dependant" ] );

		MissingPlanDependenciesException exception = Assert.Throws<MissingPlanDependenciesException>( () =>
			_project.MovePlanToRelease( "dependant", "1.0", includeDependencies: true )
		);

		Assert.Equal( [ "base" ], exception.MissingDependencies.Keys );
		Assert.Equal( "1.1", ReleaseOf( "dependant" ) );
		Assert.Equal( "1.1", ReleaseOf( "base" ) );
	}

	[Fact]
	public void AMoveLeavingADependantInAnEarlierReleaseIsRefused()
	{
		Plan( "base" );
		Plan( "dependant", "base" );
		TwoReleases( [ "base", "dependant" ] );

		StrandedPlanDependantsException exception = Assert.Throws<StrandedPlanDependantsException>( () =>
			_project.MovePlanToRelease( "base", "1.1", includeDependencies: false )
		);

		Assert.Equal( "base", exception.Plan );
		Assert.Equal( "1.1", exception.Release );
		Assert.Equal( [ new PlanDependant( "dependant", "1.0" ) ], exception.Dependants );
		Assert.Equal( "1.0", ReleaseOf( "base" ) );
		Assert.Equal( "1.0", ReleaseOf( "dependant" ) );
	}

	[Fact]
	public void ADependantInTheTargetOrALaterReleaseDoesNotBlockTheMove()
	{
		Plan( "base" );
		Plan( "same", "base" );
		Plan( "later", "base" );
		Plan( "unreleased", "base" );
		_project.CreateRelease( Release( "1.0" ), [ "base" ] );
		_project.CreateRelease( Release( "1.1", "1.0" ), [ "same" ] );
		_project.CreateRelease( Release( "1.2", "1.1" ), [ "later" ] );

		_project.MovePlanToRelease( "base", "1.1", includeDependencies: false );

		Assert.Equal( "1.1", ReleaseOf( "base" ) );
		Assert.Equal( "1.1", ReleaseOf( "same" ) );
		Assert.Equal( "1.2", ReleaseOf( "later" ) );
		Assert.Null( ReleaseOf( "unreleased" ) );
	}

	[Fact]
	public void AMoveToAnEarlierReleaseWithItsDependencyThereSucceeds()
	{
		Plan( "base" );
		Plan( "dependant", "base" );
		TwoReleases( [ "base" ], [ "dependant" ] );

		_project.MovePlanToRelease( "dependant", "1.0", includeDependencies: false );

		Assert.Equal( "1.0", ReleaseOf( "dependant" ) );
	}

	[Fact]
	public void WithDependenciesAMoveLeavesReleasedDependenciesWhereTheyAre()
	{
		Plan( "c" );
		Plan( "b", "c" );
		Plan( "a", "b" );
		TwoReleases( [ "c", "b", "a" ] );

		_project.MovePlanToRelease( "a", "1.1", includeDependencies: true );

		Assert.Equal( "1.1", ReleaseOf( "a" ) );
		Assert.Equal( "1.0", ReleaseOf( "b" ) );
		Assert.Equal( "1.0", ReleaseOf( "c" ) );
	}

	[Theory]
	[InlineData( true )]
	[InlineData( false )]
	public void AnUnreleasedPlanIsAddedAsAddPlanToReleaseWould( bool includeDependencies )
	{
		Plan( "base" );
		Plan( "dependant", "base" );
		TwoReleases();

		if( includeDependencies )
		{
			_project.MovePlanToRelease( "dependant", "1.0", includeDependencies );

			Assert.Equal( "1.0", ReleaseOf( "dependant" ) );
			Assert.Equal( "1.0", ReleaseOf( "base" ) );
		}
		else
		{
			Assert.Throws<MissingPlanDependenciesException>( () =>
				_project.MovePlanToRelease( "dependant", "1.0", includeDependencies )
			);

			Assert.Null( ReleaseOf( "dependant" ) );
			Assert.Null( ReleaseOf( "base" ) );
		}
	}

	[Fact]
	public void AMoveOutOfAFinalizedReleaseIsRefused()
	{
		Plan( "frozen" );
		_project.CreateRelease( Release( "1.0" ), [ "frozen" ] );
		_project.FinalizeRelease( "1.0" );
		_project.CreateRelease( Release( "1.1", "1.0" ) );

		Assert.Throws<ReleaseFinalizedException>( () =>
			_project.MovePlanToRelease( "frozen", "1.1", includeDependencies: false )
		);

		Assert.Equal( "1.0", ReleaseOf( "frozen" ) );
	}

	[Fact]
	public void AMoveIntoAFinalizedReleaseIsRefused()
	{
		Plan( "frozen" );
		Plan( "movable" );
		_project.CreateRelease( Release( "1.0" ), [ "frozen" ] );
		_project.FinalizeRelease( "1.0" );
		_project.CreateRelease( Release( "1.1", "1.0" ), [ "movable" ] );

		Assert.Throws<ReleaseFinalizedException>( () =>
			_project.MovePlanToRelease( "movable", "1.0", includeDependencies: false )
		);

		Assert.Equal( "1.1", ReleaseOf( "movable" ) );
	}

	[Fact]
	public void AMoveToThePlansOwnReleaseChangesNothing()
	{
		Plan( "staying" );
		TwoReleases( [ "staying" ] );

		_project.MovePlanToRelease( "staying", "1.0", includeDependencies: false );

		Assert.Equal( "1.0", ReleaseOf( "staying" ) );
	}

	[Fact]
	public void AnUnknownPlanOrReleaseIsRefused()
	{
		Plan( "movable" );
		TwoReleases( [ "movable" ] );

		Assert.Throws<UnknownPlanException>( () =>
			_project.MovePlanToRelease( "missing", "1.1", includeDependencies: false )
		);

		Assert.Throws<UnknownReleaseException>( () =>
			_project.MovePlanToRelease( "movable", "missing", includeDependencies: false )
		);

		Assert.Equal( "1.0", ReleaseOf( "movable" ) );
	}
}
