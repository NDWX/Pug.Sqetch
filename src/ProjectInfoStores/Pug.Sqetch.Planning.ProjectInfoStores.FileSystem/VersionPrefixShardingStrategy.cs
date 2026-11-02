namespace Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

/// <summary>
/// Shards finalized releases by the leading segment of the release name, e.g. with the
/// default '.' delimiter finalized release '2026.07' lives in 'releases/2026/2026.07'.
/// Names without the delimiter shard under their own full name.
/// </summary>
public sealed class VersionPrefixShardingStrategy : IReleaseShardingStrategy
{
	public const string StrategyName = "version-prefix";
	public const string DelimiterOption = "delimiter";

	private readonly string _delimiter;

	public VersionPrefixShardingStrategy( string delimiter = "." )
	{
		ArgumentException.ThrowIfNullOrEmpty( delimiter );

		_delimiter = delimiter;
	}

	public static VersionPrefixShardingStrategy FromOptions( IReadOnlyDictionary<string, string>? options )
		=> new(
			options is not null && options.TryGetValue( DelimiterOption, out string? delimiter ) ? delimiter : "."
		);

	public int ShardDepth => 1;

	public string GetShardPath( string releaseName )
	{
		int delimiterIndex = releaseName.IndexOf( _delimiter, StringComparison.Ordinal );

		return delimiterIndex > 0 ? releaseName[..delimiterIndex] : releaseName;
	}

	public bool MayContainMatch( string shardPath, string releaseNamePrefix )
	{
		if( string.IsNullOrEmpty( releaseNamePrefix ) )
			return true;

		int delimiterIndex = releaseNamePrefix.IndexOf( _delimiter, StringComparison.Ordinal );

		// a prefix containing the delimiter carries a complete first segment which must
		// match the shard exactly; otherwise the shard need only start with the prefix
		return delimiterIndex > 0 ?
			string.Equals( shardPath, releaseNamePrefix[..delimiterIndex], StringComparison.OrdinalIgnoreCase ) :
			shardPath.StartsWith( releaseNamePrefix, StringComparison.OrdinalIgnoreCase );
	}
}