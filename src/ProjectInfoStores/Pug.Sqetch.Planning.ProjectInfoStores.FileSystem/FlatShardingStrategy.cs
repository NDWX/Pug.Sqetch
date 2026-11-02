namespace Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

/// <summary>No sharding: every release folder sits directly under 'releases'.</summary>
public sealed class FlatShardingStrategy : IReleaseShardingStrategy
{
	public const string StrategyName = "flat";

	public int ShardDepth => 0;

	public string GetShardPath( string releaseName ) => string.Empty;

	public bool MayContainMatch( string shardPath, string releaseNamePrefix ) => true;
}