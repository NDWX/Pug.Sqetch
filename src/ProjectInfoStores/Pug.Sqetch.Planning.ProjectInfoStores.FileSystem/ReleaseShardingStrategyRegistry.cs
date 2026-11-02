namespace Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

/// <summary>
/// Resolves the sharding strategy named in a project file. Custom strategies can be
/// plugged in by registering a factory under the name persisted in the project file and
/// passing the registry when opening the project.
/// </summary>
public class ReleaseShardingStrategyRegistry
{
	private readonly Dictionary<string, Func<IReadOnlyDictionary<string, string>?, IReleaseShardingStrategy>>
		_factories =
			new( StringComparer.OrdinalIgnoreCase );

	public ReleaseShardingStrategyRegistry()
	{
		Register( FlatShardingStrategy.StrategyName, _ => new FlatShardingStrategy() );
		Register( VersionPrefixShardingStrategy.StrategyName, VersionPrefixShardingStrategy.FromOptions );
	}

	public void Register( string name, Func<IReadOnlyDictionary<string, string>?, IReleaseShardingStrategy> factory )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( name );
		ArgumentNullException.ThrowIfNull( factory );

		_factories[name] = factory;
	}

	public IReleaseShardingStrategy Create( ShardingConfiguration? configuration )
	{
		if( configuration is null )
			return new FlatShardingStrategy();

		if( !_factories.TryGetValue(
				configuration.Strategy,
				out Func<IReadOnlyDictionary<string, string>?, IReleaseShardingStrategy>? factory
			) )
			throw new ProjectStoreException(
				$"Unknown release sharding strategy '{configuration.Strategy}'; register it before opening the project."
			);

		return factory( configuration.Options );
	}
}