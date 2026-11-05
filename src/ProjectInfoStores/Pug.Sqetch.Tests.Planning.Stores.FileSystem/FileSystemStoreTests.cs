using Pug.Sqetch.Planning;
using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

namespace Pug.Sqetch.Tests.Planning.Stores.FileSystem;

public class FileSystemStoreTests
{
	[Theory]
	[MemberData( nameof(ShardingCases.Names), MemberType = typeof(ShardingCases) )]
	public void ProjectDefinitionRoundTrips( string sharding )
	{
		using TempProject project = TempProject.Create( ShardingCases.Configuration( sharding ) );
		using FileSystemProjectStores stores = project.Open();

		ProjectDefinition definition = stores.InfoStore.GetDefinition();

		Assert.Equal( "test-project", definition.Name );
		Assert.Equal( "postgres", definition.Engine );
	}

	[Theory]
	[MemberData( nameof(ShardingCases.Names), MemberType = typeof(ShardingCases) )]
	public void PlansAreStoredUnderPlansDirectoryUntilReleased( string sharding )
	{
		using TempProject project = TempProject.Create( ShardingCases.Configuration( sharding ) );
		using FileSystemProjectStores stores = project.Open();

		stores.InfoStore.AddPlan(
			new ObjectDefinition( "customer-email", "Add e-mail column" ),
			[ ],
			TestData.Context()
		);

		Assert.True( stores.InfoStore.PlanExists( "customer-email" ) );
		Assert.True( File.Exists( Path.Combine( project.Root, "plans", "customer-email", "plan.json" ) ) );

		ProjectPlan? plan = stores.InfoStore.GetPlan( "customer-email" );

		Assert.NotNull( plan );
		Assert.Equal( string.Empty, plan.Release );
		Assert.Equal( "Add e-mail column", plan.Definition.Description );
		Assert.Equal( TestData.User, plan.Registration.Subject );

		Assert.Throws<DuplicatePlanNameException>( () => stores.InfoStore.AddPlan(
				new ObjectDefinition( "customer-email", "again" ),
				[ ],
				TestData.Context()
			)
		);
	}

	[Theory]
	[InlineData( "bad/name" )]
	[InlineData( "../evil" )]
	[InlineData( ".hidden" )]
	[InlineData( "trailing." )]
	[InlineData( "" )]
	public void InvalidNamesAreRejected( string name )
	{
		using TempProject project = TempProject.Create();
		using FileSystemProjectStores stores = project.Open();

		Assert.Throws<ArgumentException>( () => stores.InfoStore.AddPlan(
				new ObjectDefinition( name, "" ),
				[ ],
				TestData.Context()
			)
		);
	}

	[Theory]
	[MemberData( nameof(ShardingCases.Names), MemberType = typeof(ShardingCases) )]
	public void StepsLiveInsideThePlanFolder( string sharding )
	{
		using TempProject project = TempProject.Create( ShardingCases.Configuration( sharding ) );
		using FileSystemProjectStores stores = project.Open();

		stores.InfoStore.AddPlan( new ObjectDefinition( "customer-email", "" ), [ ], TestData.Context() );

		stores.InfoStore.AddStep(
			"customer-email",
			new ObjectDefinition( "add-column", "adds the column" ),
			[ ],
			TestData.Context()
		);

		stores.InfoStore.AddStep(
			"customer-email",
			new ObjectDefinition( "backfill", "" ),
			[ "add-column" ],
			TestData.Context()
		);

		Assert.True( stores.InfoStore.StepExists( "customer-email", "add-column" ) );

		Assert.True(
			File.Exists( Path.Combine( project.Root, "plans", "customer-email", "steps", "add-column", "step.json" ) )
		);

		Assert.Equal( 2, stores.InfoStore.GetSteps( "customer-email" ).Count );
		Assert.Equal( [ "add-column" ], stores.InfoStore.GetStepDependencies( "customer-email", "backfill" ) );
		Assert.Equal( [ "backfill" ], stores.InfoStore.GetStepDependants( "customer-email", "add-column" ) );

		Assert.Throws<DuplicateStepNameException>( () => stores.InfoStore.AddStep(
				"customer-email",
				new ObjectDefinition( "add-column", "" ),
				[ ],
				TestData.Context()
			)
		);

		stores.InfoStore.DeleteStep( "customer-email", "backfill" );

		Assert.False( stores.InfoStore.StepExists( "customer-email", "backfill" ) );
	}

	[Theory]
	[MemberData( nameof(ShardingCases.Names), MemberType = typeof(ShardingCases) )]
	public void StepScriptsRoundTrip( string sharding )
	{
		using TempProject project = TempProject.Create( ShardingCases.Configuration( sharding ) );
		using FileSystemProjectStores stores = project.Open();

		stores.InfoStore.AddPlan( new ObjectDefinition( "customer-email", "" ), [ ], TestData.Context() );
		stores.InfoStore.AddStep( "customer-email", new ObjectDefinition( "add-column", "" ), [ ], TestData.Context() );

		using MemoryStream deploy = new( "ALTER TABLE customer ADD email text;"u8.ToArray() );
		using MemoryStream rollback = new( "ALTER TABLE customer DROP COLUMN email;"u8.ToArray() );

		StepScriptKeys keys = stores.ScriptsStore.PutStepScripts(
			"customer-email",
			"add-column",
			new StepScripts( deploy, null, rollback )
		);

		Assert.Equal( "plans/customer-email/steps/add-column/deploy.sql", keys.DeployScript );

		using StepScripts? scripts = stores.ScriptsStore.GetStepScripts( "customer-email", "add-column" );

		Assert.NotNull( scripts );
		Assert.NotNull( scripts.DeployScript );
		Assert.Null( scripts.VerifyScript );

		using StreamReader reader = new( scripts.DeployScript );
		Assert.Equal( "ALTER TABLE customer ADD email text;", reader.ReadToEnd() );
	}

	[Theory]
	[MemberData( nameof(ShardingCases.Names), MemberType = typeof(ShardingCases) )]
	public void AddingPlanToReleaseMovesThePlanFolder( string sharding )
	{
		using TempProject project = TempProject.Create( ShardingCases.Configuration( sharding ) );
		using FileSystemProjectStores stores = project.Open();

		stores.InfoStore.AddPlan( new ObjectDefinition( "customer-email", "" ), [ ], TestData.Context() );
		stores.InfoStore.AddStep( "customer-email", new ObjectDefinition( "add-column", "" ), [ ], TestData.Context() );
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.07", "July release", "" ), TestData.Context() );

		stores.InfoStore.AddReleasePlan( "2026.07", "customer-email", TestData.Context() );

		// unfinalized releases are never sharded, whatever the strategy
		string releaseDirectory = Path.Combine( project.Root, "releases", "2026.07" );

		Assert.True( File.Exists( Path.Combine( releaseDirectory, "plans", "customer-email", "plan.json" ) ) );

		Assert.True(
			File.Exists(
				Path.Combine( releaseDirectory, "plans", "customer-email", "steps", "add-column", "step.json" )
			)
		);

		Assert.False( Directory.Exists( Path.Combine( project.Root, "plans", "customer-email" ) ) );

		Assert.Equal( "2026.07", stores.InfoStore.GetPlan( "customer-email" )!.Release );
		Assert.Single( stores.InfoStore.GetReleasePlans( "2026.07" ) );
		Assert.Empty( stores.InfoStore.GetPlans( string.Empty ) );

		// steps and scripts remain reachable at the new location
		Assert.True( stores.InfoStore.StepExists( "customer-email", "add-column" ) );

		Assert.Throws<InvalidOperationException>( () =>
			stores.InfoStore.AddReleasePlan( "2026.07", "customer-email", TestData.Context() )
		);

		stores.InfoStore.DeleteReleasePlan( "2026.07", "customer-email", TestData.Context() );

		Assert.True( File.Exists( Path.Combine( project.Root, "plans", "customer-email", "plan.json" ) ) );
		Assert.Equal( string.Empty, stores.InfoStore.GetPlan( "customer-email" )!.Release );
	}

	[Theory]
	[MemberData( nameof(ShardingCases.Names), MemberType = typeof(ShardingCases) )]
	public void FinalizedReleaseRefusesWrites( string sharding )
	{
		using TempProject project = TempProject.Create( ShardingCases.Configuration( sharding ) );
		using FileSystemProjectStores stores = project.Open();

		stores.InfoStore.AddPlan( new ObjectDefinition( "customer-email", "" ), [ ], TestData.Context() );
		stores.InfoStore.AddStep( "customer-email", new ObjectDefinition( "add-column", "" ), [ ], TestData.Context() );
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.07", "", "" ), TestData.Context() );
		stores.InfoStore.AddReleasePlan( "2026.07", "customer-email", TestData.Context() );

		stores.InfoStore.SetReleaseContext( "2026.07", TestData.Context() );

		// finalization moves the release into its shard; everything stays reachable there
		string finalizedDirectory = sharding == FlatShardingStrategy.StrategyName ?
			Path.Combine( project.Root, "releases", "2026.07" ) :
			Path.Combine( project.Root, "releases", "2026", "2026.07" );

		Assert.True( File.Exists( Path.Combine( finalizedDirectory, "release.json" ) ) );
		Assert.True( File.Exists( Path.Combine( finalizedDirectory, "plans", "customer-email", "plan.json" ) ) );

		if( sharding != FlatShardingStrategy.StrategyName )
			Assert.False( Directory.Exists( Path.Combine( project.Root, "releases", "2026.07" ) ) );

		Assert.NotNull( stores.InfoStore.GetRelease( "2026.07" )!.Finalized );
		Assert.True( stores.InfoStore.ReleaseExists( "2026.07" ) );
		Assert.True( stores.InfoStore.StepExists( "customer-email", "add-column" ) );

		Assert.Throws<ReleaseFinalizedException>( () =>
			stores.InfoStore.SetReleaseContext( "2026.07", TestData.Context() )
		);

		Assert.Throws<ReleaseFinalizedException>( () =>
			stores.InfoStore.UpdatePlan( new PlanDefinition( "customer-email", "new", [ ] ) )
		);

		Assert.Throws<ReleaseFinalizedException>( () => stores.InfoStore.AddStep(
				"customer-email",
				new ObjectDefinition( "another", "" ),
				[ ],
				TestData.Context()
			)
		);

		Assert.Throws<ReleaseFinalizedException>( () => stores.InfoStore.DeletePlan( "customer-email" ) );

		Assert.Throws<ReleaseFinalizedException>( () =>
			stores.InfoStore.DeleteReleasePlan( "2026.07", "customer-email", TestData.Context() )
		);

		Assert.Throws<ReleaseFinalizedException>( () => stores.ScriptsStore.PutStepScripts(
				"customer-email",
				"add-column",
				new StepScripts( null, null, null )
			)
		);
	}

	[Theory]
	[MemberData( nameof(ShardingCases.Names), MemberType = typeof(ShardingCases) )]
	public void ReleasesAreFilteredByPrefixAndState( string sharding )
	{
		using TempProject project = TempProject.Create( ShardingCases.Configuration( sharding ) );
		using FileSystemProjectStores stores = project.Open();

		// with no name: whether any release exists at all
		Assert.False( stores.InfoStore.ReleaseExists() );

		stores.InfoStore.AddRelease( new ReleaseDefinition( "2025.12", "", "" ), TestData.Context() );
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.01", "", "" ), TestData.Context() );
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.07", "", "2026.01" ), TestData.Context() );

		stores.InfoStore.SetReleaseContext( "2026.01", TestData.Context() );

		Assert.True( stores.InfoStore.ReleaseExists() );
		Assert.True( stores.InfoStore.ReleaseExists( "2026.01" ) );
		Assert.False( stores.InfoStore.ReleaseExists( "2026.02" ) );

		Assert.Equal(
			[ "2026.07" ],
			stores.InfoStore.ListReleases( new ReleaseSearchCriteria( "2026" ) )
				.Select( x => x.Definition.Name )
				.Order()
				.ToArray()
		);

		Assert.Equal(
			[ "2026.01" ],
			stores.InfoStore.ListReleases( new ReleaseSearchCriteria( "2026", Finalized: true ) )
				.Select( x => x.Definition.Name )
				.ToArray()
		);

		Assert.Equal(
			[ "2025.12", "2026.07" ],
			stores.InfoStore.ListReleases( new ReleaseSearchCriteria() )
				.Select( x => x.Definition.Name )
				.Order()
				.ToArray()
		);

		Assert.Equal(
			[ "2026.07" ],
			stores.InfoStore.GetReleaseDependant( "2026.01" ).Select( x => x.Definition.Name ).ToArray()
		);

		Assert.Throws<DuplicateReleaseNameException>( () =>
			stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.01", "", "" ), TestData.Context() )
		);
	}

	[Fact]
	public void ReleaseNamedLikeItsShardFinalizesIntoItsOwnShard()
	{
		using TempProject project =
			TempProject.Create( ShardingCases.Configuration( VersionPrefixShardingStrategy.StrategyName ) );

		using FileSystemProjectStores stores = project.Open();

		// '2026' shards under '2026', so its finalized folder is a child of its unfinalized one
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026", "", "" ), TestData.Context() );

		Assert.True( File.Exists( Path.Combine( project.Root, "releases", "2026", "release.json" ) ) );

		stores.InfoStore.SetReleaseContext( "2026", TestData.Context() );

		Assert.True( File.Exists( Path.Combine( project.Root, "releases", "2026", "2026", "release.json" ) ) );
		Assert.False( File.Exists( Path.Combine( project.Root, "releases", "2026", "release.json" ) ) );
		Assert.NotNull( stores.InfoStore.GetRelease( "2026" )!.Finalized );

		Assert.Equal(
			[ "2026" ],
			stores.InfoStore.ListReleases( new ReleaseSearchCriteria( Finalized: true ) )
				.Select( x => x.Definition.Name )
				.ToArray()
		);
	}

	[Theory]
	[MemberData( nameof(ShardingCases.Names), MemberType = typeof(ShardingCases) )]
	public void PlanDependenciesAreAnsweredFromTheIndex( string sharding )
	{
		using TempProject project = TempProject.Create( ShardingCases.Configuration( sharding ) );
		using FileSystemProjectStores stores = project.Open();

		stores.InfoStore.AddPlan( new ObjectDefinition( "base", "" ), [ ], TestData.Context() );
		stores.InfoStore.AddPlan( new ObjectDefinition( "dependant-a", "" ), [ "base" ], TestData.Context() );

		stores.InfoStore.AddPlan(
			new ObjectDefinition( "dependant-b", "" ),
			[ "base", "dependant-a" ],
			TestData.Context()
		);

		// move a dependant into a release: reverse lookup must still see it
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.07", "", "" ), TestData.Context() );
		stores.InfoStore.AddReleasePlan( "2026.07", "dependant-a", TestData.Context() );

		Assert.Equal(
			[ "dependant-a", "dependant-b" ],
			stores.InfoStore.GetPlanDependants( "base" ).Order().ToArray()
		);

		Assert.Equal( [ "base", "dependant-a" ], stores.InfoStore.GetPlanDependencies( "dependant-b" ) );

		stores.InfoStore.UpdatePlan( new PlanDefinition( "dependant-b", "", [ "base" ] ) );

		Assert.Equal( [ "base" ], stores.InfoStore.GetPlanDependencies( "dependant-b" ) );
		Assert.Empty( stores.InfoStore.GetPlanDependants( "dependant-a" ) );
	}

	[Theory]
	[MemberData( nameof(ShardingCases.Names), MemberType = typeof(ShardingCases) )]
	public void DeletePlanRemovesFolderAndIndexEntry( string sharding )
	{
		using TempProject project = TempProject.Create( ShardingCases.Configuration( sharding ) );
		using FileSystemProjectStores stores = project.Open();

		stores.InfoStore.AddPlan( new ObjectDefinition( "customer-email", "" ), [ ], TestData.Context() );
		stores.InfoStore.AddStep( "customer-email", new ObjectDefinition( "add-column", "" ), [ ], TestData.Context() );

		stores.InfoStore.DeletePlan( "customer-email" );

		Assert.False( stores.InfoStore.PlanExists( "customer-email" ) );
		Assert.False( Directory.Exists( Path.Combine( project.Root, "plans", "customer-email" ) ) );
		Assert.DoesNotContain( "customer-email", File.ReadAllText( Path.Combine( project.Root, "plan-index" ) ) );
	}
}