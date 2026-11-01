namespace Pug.Sqetch.Deployment.Bundling.BundleTypes;

/// <summary>
/// Reads bundle entries from a directory tree. Serves the directory bundle type in place
/// and the archive types through their temporary extraction directories, which it deletes
/// on dispose.
/// </summary>
internal sealed class DirectoryBundleReader( string root, bool deleteOnDispose ) : IBundleReader
{
	public IEnumerable<string> Entries
		=> Directory.EnumerateFiles( root, "*", SearchOption.AllDirectories )
					.Select( file => Path.GetRelativePath( root, file )
										.Replace( Path.DirectorySeparatorChar, '/' ) );

	public bool Contains( string path ) => File.Exists( Target( path ) );

	public Stream Open( string path )
	{
		if( !Contains( path ) )
			throw new BundlingException( $"Bundle has no entry '{path}'." );

		return new FileStream( Target( path ), FileMode.Open, FileAccess.Read );
	}

	public void Dispose()
	{
		if( !deleteOnDispose )
			return;

		try
		{
			Directory.Delete( root, recursive: true );
		}
		catch( IOException )
		{
		}
	}

	private string Target( string path )
		=> Path.Combine( root, path.Replace( '/', Path.DirectorySeparatorChar ) );
}
