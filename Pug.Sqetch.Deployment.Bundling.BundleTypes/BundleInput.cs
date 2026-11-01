namespace Pug.Sqetch.Deployment.Bundling.BundleTypes;

internal static class BundleInput
{
	/// <summary>
	/// Extracts an archive bundle into a fresh temporary directory using
	/// <paramref name="extract"/> and returns a reader over it that deletes the directory
	/// on dispose. A missing archive surfaces as <see cref="MissingBundleException"/> and an
	/// unreadable one as a friendly <see cref="BundlingException"/>.
	/// </summary>
	public static IBundleReader ExtractToTemporary( string path, Action<string, string> extract )
	{
		if( !File.Exists( path ) )
			throw new MissingBundleException( path );

		string temporary = Path.Combine( Path.GetTempPath(), "sqetch-bundle-" + Guid.NewGuid().ToString( "N" ) );

		Directory.CreateDirectory( temporary );

		try
		{
			extract( path, temporary );
		}
		catch( Exception exception ) when( exception is IOException or InvalidDataException )
		{
			try
			{
				Directory.Delete( temporary, recursive: true );
			}
			catch( IOException )
			{
			}

			throw new BundlingException( $"Cannot read bundle '{path}': {exception.Message}" );
		}

		return new DirectoryBundleReader( temporary, deleteOnDispose: true );
	}
}
