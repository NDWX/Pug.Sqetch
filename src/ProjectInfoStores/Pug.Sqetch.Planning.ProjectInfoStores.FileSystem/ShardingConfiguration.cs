namespace Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

/// <summary>
/// Persisted (in the project file) selection of a release sharding strategy plus its
/// strategy-specific options. Resolved through <see cref="ReleaseShardingStrategyRegistry"/>.
/// </summary>
public record ShardingConfiguration( string Strategy, IReadOnlyDictionary<string, string>? Options = null );