using Pug.Sqetch.Models;
using Pug.Sqetch.ProjectInfoStore;

namespace Pug.Sqetch.ProjectInfoStores.FileSystem;

/// <summary>
/// Entry point for the file-system project store: initializes a project directory or
/// opens an existing one, exposing the <see cref="IProjectInfoStore"/> and
/// <see cref="IScriptsStore"/> pair backed by that directory.
/// </summary>
public sealed class FileSystemProjectStores : IDisposable
{
	/// <summary>
	/// The project format this store writes and the highest it reads. Raise it whenever a
	/// change to the on-disk layout would make an older store misread or damage a project, so
	/// that the older store refuses the project instead.
	/// </summary>
	public const int Format = 1;

	private readonly ProjectStoreSession _session;

	private FileSystemProjectStores( ProjectStoreSession session )
	{
		_session = session;
		InfoStore = new FileSystemProjectInfoStore( session );
		ScriptsStore = new FileSystemScriptsStore( session );
	}

	public IProjectInfoStore InfoStore { get; }

	public IScriptsStore ScriptsStore { get; }

	/// <summary>
	/// The project's on-disk layout, for hosts that deliberately couple to the file-system
	/// store and need to surface locations (e.g. the CLI printing a created plan's path).
	/// </summary>
	public ProjectPaths Paths => _session.Paths;

	public static FileSystemProjectStores Open( string path, ReleaseShardingStrategyRegistry? shardingStrategies = null )
		=> new ( new ProjectStoreSession( path, shardingStrategies ?? new ReleaseShardingStrategyRegistry() ) );

	public static FileSystemProjectStores Initialize(
		ProjectDefinition definition, ActionContext context, string path,
		ShardingConfiguration? releaseSharding = null, ReleaseShardingStrategyRegistry? shardingStrategies = null )
	{
		ArgumentNullException.ThrowIfNull( definition );
		ArgumentNullException.ThrowIfNull( context );
		NameRules.Ensure( definition.Name, "Project" );

		string root = Path.GetFullPath( path );

		if( File.Exists( Path.Combine( root, FileNames.ProjectFile ) ) )
			throw new ProjectStoreException( $"'{root}' already contains a Sqetch project." );

		ReleaseShardingStrategyRegistry registry = shardingStrategies ?? new ReleaseShardingStrategyRegistry();

		ShardingConfiguration sharding = releaseSharding ?? new ShardingConfiguration( FlatShardingStrategy.StrategyName );

		// resolve before persisting so an unknown strategy name fails here, not on open
		registry.Create( sharding );

		Directory.CreateDirectory( Path.Combine( root, FileNames.PlansDirectory ) );
		Directory.CreateDirectory( Path.Combine( root, FileNames.ReleasesDirectory ) );

		JsonFiles.Write(
			Path.Combine( root, FileNames.ProjectFile ),
			new ProjectDocument(
				Format,
				definition.Name, definition.Description, definition.Engine,
				ActionDocument.From( context ), sharding ) );

		AtomicFile.WriteAllText( Path.Combine( root, FileNames.PlanIndexFile ), string.Empty );

		return Open( root, registry );
	}

	/// <summary>
	/// Rebuilds the derived 'plan-index' file from the plan folders — recovery path for a
	/// mangled merge or manual edits.
	/// </summary>
	public void Reindex() => _session.Reindex();

	public void Dispose()
	{
		InfoStore.Dispose();
		ScriptsStore.Dispose();
	}
}
