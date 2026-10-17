using Pug.Sqetch.ProjectInfoStores.FileSystem;

namespace Pug.Sqetch.Cli.Commands.Authoring;

/// <summary>
/// One opened Sqetch project for the duration of a command: the file-system stores, the
/// on-disk layout (deliberate CLI-level coupling to the file-system store, for printing
/// created locations), and the lazily wired business-logic engine.
/// </summary>
internal sealed class ProjectSession : IDisposable
{
	private IProject? _project;

	private IReadOnlyProject? _readOnlyProject;

	private ProjectSession( string root, FileSystemProjectStores stores )
	{
		Root = root;
		Stores = stores;
	}

	public string Root { get; }

	public FileSystemProjectStores Stores { get; }

	public ProjectPaths Paths => Stores.Paths;

	public IProject Project
		=> _project ??= ProjectFactory.Create(
			Stores.InfoStore, Stores.ScriptsStore,
			new ReleaseChainDependencyDeterminator( Stores.InfoStore ),
			UserIdentity.Require( Root ) );

	/// <summary>
	/// The project for reading only, which needs no project user. Reuses <see cref="Project"/>
	/// when a command has already opened it, so one session never holds two engines over the
	/// same stores.
	/// </summary>
	public IReadOnlyProject ReadOnlyProject
		=> (IReadOnlyProject?)_project
			?? ( _readOnlyProject ??= ProjectFactory.CreateReadOnly(
					Stores.InfoStore, Stores.ScriptsStore,
					new ReleaseChainDependencyDeterminator( Stores.InfoStore ) ) );

	public static ProjectSession Open() => Open( Directory.GetCurrentDirectory() );

	public static ProjectSession Open( string root )
		=> new ( root, FileSystemProjectStores.Open( root ) );

	public void Dispose()
	{
		_project?.Dispose();
		_readOnlyProject?.Dispose();
		Stores.Dispose();
	}
}
