using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

/// <summary>
/// Stores a step's deploy/verify/rollback scripts as plain .sql files inside the step's
/// folder, so script changes diff natively in version control and a finalized release's
/// folder freezes its scripts together with its metadata. Keys are project-root-relative
/// paths with '/' separators.
/// </summary>
public sealed class FileSystemScriptsStore : IScriptsStore
{
	private readonly ProjectStoreSession _session;

	internal FileSystemScriptsStore( ProjectStoreSession session )
	{
		_session = session;
	}

	public StepScriptKeys PutStepScripts( string plan, string step, StepScripts scripts )
	{
		NameRules.Ensure( step, "Step" );

		PlanIndexEntry entry = _session.RequirePlan( plan );

		_session.EnsureReleaseNotFinalized( entry.Release );

		string stepDirectory = _session.Paths.StepDirectory( entry.Release, entry.Name, step );

		Directory.CreateDirectory( stepDirectory );

		WriteScript( Path.Combine( stepDirectory, FileNames.DeployScriptFile ), scripts.DeployScript );
		WriteScript( Path.Combine( stepDirectory, FileNames.VerifyScriptFile ), scripts.VerifyScript );
		WriteScript( Path.Combine( stepDirectory, FileNames.RollbackScriptFile ), scripts.RollbackScript );

		return GetKeys( stepDirectory );
	}

	public void DeleteStepScripts( string plan, string step )
	{
		PlanIndexEntry? entry = _session.PlanIndex.Get( plan );

		if( entry is null )
			return;

		_session.EnsureReleaseNotFinalized( entry.Release );

		string stepDirectory = _session.Paths.StepDirectory( entry.Release, entry.Name, step );

		if( !Directory.Exists( stepDirectory ) )
			return;

		File.Delete( Path.Combine( stepDirectory, FileNames.DeployScriptFile ) );
		File.Delete( Path.Combine( stepDirectory, FileNames.VerifyScriptFile ) );
		File.Delete( Path.Combine( stepDirectory, FileNames.RollbackScriptFile ) );

		// the folder itself belongs to the step definition; remove it only when the step
		// is already gone and nothing else is left behind
		if( !File.Exists( Path.Combine( stepDirectory, FileNames.StepFile ) ) &&
			!Directory.EnumerateFileSystemEntries( stepDirectory ).Any() )
			Directory.Delete( stepDirectory );
	}

	public StepScripts? GetStepScripts( string plan, string name )
	{
		PlanIndexEntry? entry = _session.PlanIndex.Get( plan );

		if( entry is null )
			return null;

		string stepDirectory = _session.Paths.StepDirectory( entry.Release, entry.Name, name );

		if( !Directory.Exists( stepDirectory ) )
			return null;

		return new StepScripts(
			OpenScript( Path.Combine( stepDirectory, FileNames.DeployScriptFile ) ),
			OpenScript( Path.Combine( stepDirectory, FileNames.VerifyScriptFile ) ),
			OpenScript( Path.Combine( stepDirectory, FileNames.RollbackScriptFile ) )
		);
	}

	public void Dispose()
	{
		// all writes are flushed eagerly; returned script streams are owned by the caller
	}

	private StepScriptKeys GetKeys( string stepDirectory )
		=> new(
			_session.Paths.RelativeToRoot( Path.Combine( stepDirectory, FileNames.DeployScriptFile ) ),
			_session.Paths.RelativeToRoot( Path.Combine( stepDirectory, FileNames.VerifyScriptFile ) ),
			_session.Paths.RelativeToRoot( Path.Combine( stepDirectory, FileNames.RollbackScriptFile ) )
		);

	private static void WriteScript( string path, Stream? content )
	{
		if( content is null )
			return;

		string temporary = $"{path}.{Guid.NewGuid():N}.tmp";

		try
		{
			using( FileStream file = File.Create( temporary ) )
				content.CopyTo( file );

			File.Move( temporary, path, overwrite: true );
		}
		catch
		{
			File.Delete( temporary );

			throw;
		}
	}

	private static Stream? OpenScript( string path )
		=> File.Exists( path ) ? File.OpenRead( path ) : null;
}