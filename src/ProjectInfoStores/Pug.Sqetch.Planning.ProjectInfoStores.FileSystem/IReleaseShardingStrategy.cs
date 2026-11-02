namespace Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

/// <summary>
/// Decides where a <em>finalized</em> release folder lives under the 'releases' directory,
/// so that directory listings stay bounded as releases accumulate. Unfinalized releases are
/// never sharded — only a few exist at a time, so they sit directly under 'releases' and
/// move into their shard at finalization. The strategy is recorded in the project file at
/// initialization time and every client of the project must resolve it the same way,
/// because it determines the physical layout shared through version control.
/// </summary>
public interface IReleaseShardingStrategy
{
	/// <summary>
	/// Number of directory levels between 'releases' and a release folder.
	/// Must be constant for the lifetime of a project.
	/// </summary>
	int ShardDepth { get; }

	/// <summary>
	/// Relative shard path (exactly <see cref="ShardDepth"/> levels, '/'-separated;
	/// empty when <see cref="ShardDepth"/> is 0) for the given release name.
	/// </summary>
	string GetShardPath( string releaseName );

	/// <summary>
	/// Whether the given shard could contain releases whose name starts with
	/// <paramref name="releaseNamePrefix"/>. Used to narrow enumeration to candidate
	/// shards; returning true for a non-matching shard is safe but slower.
	/// </summary>
	bool MayContainMatch( string shardPath, string releaseNamePrefix );
}