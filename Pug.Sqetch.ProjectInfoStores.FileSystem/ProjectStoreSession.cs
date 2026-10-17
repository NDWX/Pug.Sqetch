namespace Pug.Sqetch.ProjectInfoStores.FileSystem;

/// <summary>
/// State shared by <see cref="FileSystemProjectInfoStore"/> and
/// <see cref="FileSystemScriptsStore"/> over one opened project directory: the parsed
/// project file, the resolved sharding strategy (via <see cref="ProjectPaths"/>) and the
/// lazily loaded plan index.
/// </summary>
internal sealed class ProjectStoreSession
{
	private PlanIndex? _planIndex;

	public ProjectStoreSession( string root, ReleaseShardingStrategyRegistry shardingStrategies )
	{
		root = Path.GetFullPath( root );

		string projectFile = Path.Combine( root, FileNames.ProjectFile );

		if( !File.Exists( projectFile ) )
			throw new ProjectStoreException(
				$"'{root}' is not a Sqetch project directory ('{FileNames.ProjectFile}' not found)." );

		Project = JsonFiles.Read<ProjectDocument>( projectFile );

		// checked before anything else is read, for reading and writing alike: a newer layout
		// may keep things where this store would never look, or where it would write over them
		int format = Project.Format ?? 1;

		if( format < 1 || format > FileSystemProjectStores.Format )
			throw new UnsupportedProjectFormatException( root, format, FileSystemProjectStores.Format );

		Paths = new ProjectPaths( root, shardingStrategies.Create( Project.ReleaseSharding ) );
	}

	public ProjectDocument Project { get; }

	public ProjectPaths Paths { get; }

	public PlanIndex PlanIndex => _planIndex ??= PlanIndex.Load( Paths.PlanIndexFile );

	public void Reindex() => _planIndex = PlanIndex.Rebuild( Paths );

	public PlanIndexEntry RequirePlan( string name )
		=> PlanIndex.Get( name ) ?? throw new UnknownPlanException( name );

	public ReleaseDocument? ReadRelease( string name )
		=> JsonFiles.TryRead<ReleaseDocument>( Paths.ReleaseFile( name ) );

	public ReleaseDocument RequireRelease( string name )
		=> ReadRelease( name ) ?? throw new UnknownReleaseException( name );

	/// <summary>
	/// Everything under a finalized release's folder is immutable; refuse writes that
	/// would touch it. An empty release name means the plan is unreleased.
	/// </summary>
	public void EnsureReleaseNotFinalized( string release )
	{
		if( string.IsNullOrEmpty( release ) )
			return;

		if( ReadRelease( release )?.Finalized is not null )
			throw new ReleaseFinalizedException();
	}
}
