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