using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

namespace Pug.Sqetch.Tests.Planning.Stores.FileSystem;

public class PlanIndexRebuildTests
{
	[Theory]
	[MemberData( nameof(ShardingCases.Names), MemberType = typeof(ShardingCases) )]
	public void ReindexRegeneratesTheIndexByteForByte( string sharding )
	{
		using TempProject project = TempProject.Create( ShardingCases.Configuration( sharding ) );

		using( FileSystemProjectStores stores = project.Open() )
		{
			stores.InfoStore.AddPlan( new ObjectDefinition( "base", "" ), [ ], TestData.Context() );
			stores.InfoStore.AddPlan( new ObjectDefinition( "customer-email", "" ), [ "base" ], TestData.Context() );

			stores.InfoStore.AddPlan(
				new ObjectDefinition( "orphan", "" ),
				[ "base", "customer-email" ],
				TestData.Context()
			);

			stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.07", "", "" ), TestData.Context() );
			stores.InfoStore.AddReleasePlan( "2026.07", "base", TestData.Context() );
			stores.InfoStore.AddReleasePlan( "2026.07", "customer-email", TestData.Context() );

			// finalize so the rebuild also has to find plans inside a sharded release folder
			stores.InfoStore.SetReleaseContext( "2026.07", TestData.Context() );
		}

		string indexFile = Path.Combine( project.Root, "plan-index" );
		string expected = File.ReadAllText( indexFile );

		File.WriteAllText( indexFile, "mangled by a bad merge" );

		using( FileSystemProjectStores stores = project.Open() )
		{
			stores.Reindex();

			// the rebuilt index must answer queries again immediately
			Assert.Equal( "2026.07", stores.InfoStore.GetPlan( "customer-email" )!.Release );

			Assert.Equal(
				[ "customer-email", "orphan" ],
				stores.InfoStore.GetPlanDependants( "base" ).Order().ToArray()
			);
		}

		Assert.Equal( expected, File.ReadAllText( indexFile ) );
	}

	/// <summary>
	/// A command cut short after moving a plan's folder leaves its plan.json 'release' field and
	/// the index behind; the folder is the truth, and reindex realigns both with it.
	/// </summary>
	[Theory]
	[InlineData( "2026.08" )]
	[InlineData( "" )]
	public void ReindexRealignsAPlanJsonLeftBehindByAMovedFolder( string movedTo )
	{
		using TempProject project = TempProject.Create();

		using( FileSystemProjectStores stores = project.Open() )
		{
			stores.InfoStore.AddPlan( new ObjectDefinition( "customer-email", "" ), [ ], TestData.Context() );
			stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.07", "", "" ), TestData.Context() );
			stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.08", "", "2026.07" ), TestData.Context() );
			stores.InfoStore.AddReleasePlan( "2026.07", "customer-email", TestData.Context() );
		}

		string destination = movedTo.Length == 0 ?
			Path.Combine( project.Root, "plans", "customer-email" ) :
			Path.Combine( project.Root, "releases", movedTo, "plans", "customer-email" );

		Directory.CreateDirectory( Path.GetDirectoryName( destination )! );
		Directory.Move( Path.Combine( project.Root, "releases", "2026.07", "plans", "customer-email" ), destination );

		using( FileSystemProjectStores stores = project.Open() )
		{
			stores.Reindex();

			Assert.Equal( movedTo, stores.InfoStore.GetPlan( "customer-email" )!.Release );
		}

		string planJson = File.ReadAllText( Path.Combine( destination, "plan.json" ) );

		Assert.Contains( $"\"release\": \"{movedTo}\"", planJson );

		if( movedTo.Length == 0 )
			Assert.Contains( "\"releaseAssignment\": null", planJson );
		else
			Assert.DoesNotContain( "\"releaseAssignment\": null", planJson );
	}

	[Fact]
	public void ReindexLeavesAConsistentPlanJsonUntouched()
	{
		using TempProject project = TempProject.Create();

		using( FileSystemProjectStores stores = project.Open() )
		{
			stores.InfoStore.AddPlan( new ObjectDefinition( "unreleased", "" ), [ ], TestData.Context() );
			stores.InfoStore.AddPlan( new ObjectDefinition( "released", "" ), [ ], TestData.Context() );
			stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.07", "", "" ), TestData.Context() );
			stores.InfoStore.AddReleasePlan( "2026.07", "released", TestData.Context() );
		}

		string[] planFiles = Directory.GetFiles( project.Root, "plan.json", SearchOption.AllDirectories );
		Dictionary<string, (string, DateTime)> before =
			planFiles.ToDictionary( x => x, x => ( File.ReadAllText( x ), File.GetLastWriteTimeUtc( x ) ) );

		using( FileSystemProjectStores stores = project.Open() )
			stores.Reindex();

		Assert.Equal( 2, planFiles.Length );

		foreach( string planFile in planFiles )
			Assert.Equal( before[planFile], ( File.ReadAllText( planFile ), File.GetLastWriteTimeUtc( planFile ) ) );
	}

	[Fact]
	public void CorruptIndexFailsWithARecoverableError()
	{
		using TempProject project = TempProject.Create();

		File.WriteAllText(
			Path.Combine( project.Root, "plan-index" ),
			"not\tan\tindex\tline\twith\ttoo\tmany\tfields"
		);

		using FileSystemProjectStores stores = project.Open();

		Assert.Throws<ProjectStoreException>( () => stores.InfoStore.PlanExists( "anything" ) );
	}
}