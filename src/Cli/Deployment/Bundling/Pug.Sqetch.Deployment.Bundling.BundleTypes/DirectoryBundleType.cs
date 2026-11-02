namespace Pug.Sqetch.Deployment.Bundling.BundleTypes;

/// <summary>
/// Writes the bundle as a plain directory tree instead of an archive: each entry becomes a
/// file under the output directory.
/// </summary>
public sealed class DirectoryBundleType : IBundleType
{
	public const string TypeName = "directory";

	public string Name => TypeName;

	public string DefaultOutputName( string baseName ) => baseName;

	public IBundleWriter Create( string outputPath )
	{
		if( File.Exists( outputPath ) )
			throw new BundlingException( $"Bundle output '{outputPath}' already exists." );

		if( Directory.Exists( outputPath ) && Directory.EnumerateFileSystemEntries( outputPath ).Any() )
			throw new BundlingException( $"Bundle output directory '{outputPath}' is not empty." );

		Directory.CreateDirectory( outputPath );

		return new Writer( outputPath );
	}

	public IBundleReader Open( string path )
	{
		if( !Directory.Exists( path ) )
			throw new MissingBundleException( path );

		return new DirectoryBundleReader( path, deleteOnDispose: false );
	}

	private sealed class Writer( string root ) : IBundleWriter
	{
		public void Add( string path, Stream content )
		{
			string target = Path.Combine( root, path.Replace( '/', Path.DirectorySeparatorChar ) );

			Directory.CreateDirectory( Path.GetDirectoryName( target )! );

			using FileStream file = new( target, FileMode.CreateNew, FileAccess.Write );

			content.CopyTo( file );
		}

		public void Dispose()
		{
		}
	}
}