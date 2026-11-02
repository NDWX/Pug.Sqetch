namespace Pug.Sqetch.Deployment.Bundling.BundleTypes;

internal static class BundleOutput
{
	/// <summary>
	/// Opens a new bundle output file, refusing an existing target and creating the parent
	/// directory when needed. <see cref="FileMode.CreateNew"/> backstops the existence check
	/// against races.
	/// </summary>
	public static FileStream CreateFile( string path )
	{
		if( File.Exists( path ) || Directory.Exists( path ) )
			throw new BundlingException( $"Bundle output '{path}' already exists." );

		string? parent = Path.GetDirectoryName( path );

		if( !string.IsNullOrEmpty( parent ) )
			Directory.CreateDirectory( parent );

		return new FileStream( path, FileMode.CreateNew, FileAccess.Write );
	}
}