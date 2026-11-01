using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

namespace Pug.Sqetch.Tests.Planning.Stores.FileSystem;

public class ShardingTests
{
	[Theory]
	[InlineData( "2026.07", "2026" )]
	[InlineData( "2026.07.1", "2026" )]
	[InlineData( "hotfix", "hotfix" )]
	public void NamePrefixStrategyShardsByFirstSegment( string release, string shard )
	{
		VersionPrefixShardingStrategy strategy = new ();

		Assert.Equal( shard, strategy.GetShardPath( release ) );
	}

	[Theory]
	[InlineData( "2026", "", true )]
	[InlineData( "2026", "20", true )]
	[InlineData( "2026", "2026", true )]
	[InlineData( "2026", "2026.07", true )]
	[InlineData( "2025", "2026", false )]
	[InlineData( "2025", "2026.07", false )]
	[InlineData( "2026", "3", false )]
	public void NamePrefixStrategyNarrowsCandidateShards( string shard, string prefix, bool candidate )
	{
		VersionPrefixShardingStrategy strategy = new ();

		Assert.Equal( candidate, strategy.MayContainMatch( shard, prefix ) );
	}

	[Fact]
	public void CustomStrategyPlugsInThroughTheRegistry()
	{
		ReleaseShardingStrategyRegistry registry = new ();

		registry.Register( "by-length", _ => new ByLengthStrategy() );

		using TempProject project = TempProject.Create( new ShardingConfiguration( "by-length" ), registry );

		using( FileSystemProjectStores stores = project.Open( registry ) )
		{
			stores.InfoStore.AddRelease( new ReleaseDefinition( "alpha", "", "" ), TestData.Context() );

			// sharding only applies to finalized releases
			Assert.True( File.Exists( Path.Combine( project.Root, "releases", "alpha", "release.json" ) ) );
			Assert.Equal(
				["alpha"],
				stores.InfoStore.ListReleases( new ReleaseSearchCriteria() ).Select( x => x.Definition.Name ).ToArray() );

			stores.InfoStore.SetReleaseContext( "alpha", TestData.Context() );

			Assert.True( File.Exists( Path.Combine( project.Root, "releases", "5", "alpha", "release.json" ) ) );
			Assert.False( Directory.Exists( Path.Combine( project.Root, "releases", "alpha" ) ) );
			Assert.Equal(
				["alpha"],
				stores.InfoStore.ListReleases( new ReleaseSearchCriteria( Finalized: true ) )
						.Select( x => x.Definition.Name ).ToArray() );
		}

		// opening without the custom registration must fail loudly, not misplace files
		Assert.Throws<ProjectStoreException>( () => project.Open() );
	}

	[Fact]
	public void UnknownStrategyIsRejectedAtInitialization()
	{
		string root = Path.Combine( Path.GetTempPath(), "sqetch-tests", Guid.NewGuid().ToString( "N" ) );

		Assert.Throws<ProjectStoreException>(
			() => FileSystemProjectStores.Initialize(
				new ProjectDefinition( "p", "", "postgres" ), TestData.Context(), root,
				new ShardingConfiguration( "no-such-strategy" ) ) );
	}

	private sealed class ByLengthStrategy : IReleaseShardingStrategy
	{
		public int ShardDepth => 1;

		public string GetShardPath( string releaseName ) => releaseName.Length.ToString();

		public bool MayContainMatch( string shardPath, string releaseNamePrefix )
			=> releaseNamePrefix.Length == 0 ||
				( int.TryParse( shardPath, out int length ) && length >= releaseNamePrefix.Length );
	}
}
