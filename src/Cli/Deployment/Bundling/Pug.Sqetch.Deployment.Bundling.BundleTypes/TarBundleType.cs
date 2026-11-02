using System.Formats.Tar;

namespace Pug.Sqetch.Deployment.Bundling.BundleTypes;

public sealed class TarBundleType : IBundleType
{
	public const string TypeName = "tar";

	public string Name => TypeName;

	public string DefaultOutputName( string baseName ) => baseName + ".tar";

	public IBundleWriter Create( string outputPath )
		=> new Writer( new TarWriter( BundleOutput.CreateFile( outputPath ) ), wrapper: null );

	/// <summary>Writes the archive to <paramref name="output"/>, which stays open.</summary>
	public IBundleWriter Create( Stream output )
		=> new Writer( new TarWriter( output, leaveOpen: true ), wrapper: null );

	public IBundleReader Open( string path )
		=> BundleInput.ExtractToTemporary(
			path,
			( archive, directory )
				=> TarFile.ExtractToDirectory( archive, directory, overwriteFiles: false )
		);

	/// <summary>
	/// Shared by <see cref="TarBundleType"/> and <see cref="TarGzBundleType"/>;
	/// <paramref name="wrapper"/> is an optional compression stream between the tar writer
	/// and the output, disposed after the tar writer so its footer follows the tar EOF
	/// blocks.
	/// </summary>
	internal sealed class Writer( TarWriter tar, Stream? wrapper ) : IBundleWriter
	{
		public void Add( string path, Stream content )
			=> tar.WriteEntry(
				new PaxTarEntry( TarEntryType.RegularFile, path )
				{
					DataStream = content,
					Mode = UnixFileMode.UserRead |
							UnixFileMode.UserWrite |
							UnixFileMode.GroupRead |
							UnixFileMode.OtherRead
				}
			);

		public void Dispose()
		{
			tar.Dispose();
			wrapper?.Dispose();
		}
	}
}