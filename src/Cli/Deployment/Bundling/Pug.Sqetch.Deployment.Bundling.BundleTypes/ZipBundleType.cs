using System.IO.Compression;

namespace Pug.Sqetch.Deployment.Bundling.BundleTypes;

public sealed class ZipBundleType : IBundleType
{
	public const string TypeName = "zip";

	public string Name => TypeName;

	public string DefaultOutputName( string baseName ) => baseName + ".zip";

	public IBundleWriter Create( string outputPath )
		=> new Writer( new ZipArchive( BundleOutput.CreateFile( outputPath ), ZipArchiveMode.Create ) );

	/// <summary>Writes the archive to <paramref name="output"/>, which stays open.</summary>
	public IBundleWriter Create( Stream output )
		=> new Writer( new ZipArchive( output, ZipArchiveMode.Create, leaveOpen: true ) );

	public IBundleReader Open( string path )
		=> BundleInput.ExtractToTemporary( path, ( archive, directory )
				=> ZipFile.ExtractToDirectory( archive, directory ) );

	private sealed class Writer( ZipArchive archive ) : IBundleWriter
	{
		public void Add( string path, Stream content )
		{
			using Stream entry = archive.CreateEntry( path, CompressionLevel.Optimal ).Open();

			content.CopyTo( entry );
		}

		public void Dispose() => archive.Dispose();
	}
}
