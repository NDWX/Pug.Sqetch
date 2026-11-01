using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Pug.Sqetch.Deployment.Bundling;
using Pug.Sqetch.Deployment.Bundling.BundleTypes;

namespace Pug.Sqetch.Tests.Deployment.Bundling;

public class BundleTypeTests
{
	private static MemoryStream Content( string text ) => new ( Encoding.UTF8.GetBytes( text ) );

	/// <summary>
	/// Writes two entries through <paramref name="create"/> and asserts the output stream
	/// survives the writer: still writable (left open) and non-empty (EOF blocks / central
	/// directory / gzip footer flushed).
	/// </summary>
	private static MemoryStream WriteSample( Func<Stream, IBundleWriter> create )
	{
		MemoryStream output = new ();

		using( IBundleWriter writer = create( output ) )
		{
			using( MemoryStream first = Content( "manifest" ) )
				writer.Add( "manifest.json", first );

			using( MemoryStream second = Content( "-- deploy" ) )
				writer.Add( "plans/p1/steps/s1/deploy.sql", second );
		}

		Assert.True( output.CanWrite );
		Assert.True( output.Length > 0 );

		output.Position = 0;

		return output;
	}

	[Fact]
	public void ZipRoundTripsWithForwardSlashPaths()
	{
		using MemoryStream output = WriteSample( new ZipBundleType().Create );

		using ZipArchive archive = new ( output, ZipArchiveMode.Read );

		Assert.Equal(
			["manifest.json", "plans/p1/steps/s1/deploy.sql"],
			archive.Entries.Select( x => x.FullName ).ToArray() );

		using StreamReader reader = new ( archive.GetEntry( "plans/p1/steps/s1/deploy.sql" )!.Open() );
		Assert.Equal( "-- deploy", reader.ReadToEnd() );
	}

	[Fact]
	public void TarProducesPaxRegularFileEntries()
	{
		using MemoryStream output = WriteSample( new TarBundleType().Create );

		using TarReader reader = new ( output );

		TarEntry first = reader.GetNextEntry()!;

		Assert.Equal( TarEntryFormat.Pax, first.Format );
		Assert.Equal( TarEntryType.RegularFile, first.EntryType );
		Assert.Equal( "manifest.json", first.Name );
		Assert.Equal( "manifest", new StreamReader( first.DataStream! ).ReadToEnd() );

		TarEntry second = reader.GetNextEntry()!;

		Assert.Equal( "plans/p1/steps/s1/deploy.sql", second.Name );

		Assert.Null( reader.GetNextEntry() );
	}

	[Fact]
	public void TarGzRoundTripsThroughGZip()
	{
		using MemoryStream output = WriteSample( new TarGzBundleType().Create );

		byte[] bytes = output.ToArray();
		Assert.Equal( [0x1f, 0x8b], bytes[..2] );

		using GZipStream gzip = new ( output, CompressionMode.Decompress );
		using TarReader reader = new ( gzip );

		Assert.Equal( "manifest.json", reader.GetNextEntry()!.Name );
		Assert.Equal( "plans/p1/steps/s1/deploy.sql", reader.GetNextEntry()!.Name );
		Assert.Null( reader.GetNextEntry() );
	}

	[Fact]
	public void DirectoryTypeMaterializesEntriesAsFiles()
	{
		string root = TestDirectory();

		try
		{
			string target = Path.Combine( root, "out" );

			using( IBundleWriter writer = new DirectoryBundleType().Create( target ) )
			{
				using MemoryStream manifest = Content( "manifest" );
				writer.Add( "manifest.json", manifest );

				using MemoryStream script = Content( "-- deploy" );
				writer.Add( "plans/p1/steps/s1/deploy.sql", script );
			}

			Assert.Equal( "manifest", File.ReadAllText( Path.Combine( target, "manifest.json" ) ) );
			Assert.Equal(
				"-- deploy",
				File.ReadAllText( Path.Combine( target, "plans", "p1", "steps", "s1", "deploy.sql" ) ) );
		}
		finally
		{
			Directory.Delete( root, recursive: true );
		}
	}

	[Fact]
	public void ExistingTargetsAreRefused()
	{
		string root = TestDirectory();

		try
		{
			string file = Path.Combine( root, "bundle.zip" );
			File.WriteAllText( file, "keep" );

			Assert.Contains( "already exists",
				Assert.Throws<BundlingException>( () => new ZipBundleType().Create( file ) ).Message );
			Assert.Equal( "keep", File.ReadAllText( file ) );

			// a non-empty directory target is refused; an empty one is acceptable
			string occupied = Path.Combine( root, "occupied" );
			Directory.CreateDirectory( occupied );
			File.WriteAllText( Path.Combine( occupied, "existing.txt" ), "" );

			Assert.Contains( "not empty",
				Assert.Throws<BundlingException>( () => new DirectoryBundleType().Create( occupied ) ).Message );

			Assert.Throws<BundlingException>( () => new DirectoryBundleType().Create( file ) );
		}
		finally
		{
			Directory.Delete( root, recursive: true );
		}
	}

	[Fact]
	public void DefaultOutputNamesCarryTheTypeExtension()
	{
		Assert.Equal( "demo-1.zip", new ZipBundleType().DefaultOutputName( "demo-1" ) );
		Assert.Equal( "demo-1.tar", new TarBundleType().DefaultOutputName( "demo-1" ) );
		Assert.Equal( "demo-1.tar.gz", new TarGzBundleType().DefaultOutputName( "demo-1" ) );
		Assert.Equal( "demo-1", new DirectoryBundleType().DefaultOutputName( "demo-1" ) );
	}

	[Fact]
	public void RegistryResolvesKnownNamesCaseInsensitivelyAndRejectsUnknownOnes()
	{
		BundleTypeRegistry registry = new ();
		registry.RegisterBundleTypes();

		Assert.IsType<ZipBundleType>( registry.Create( "ZIP" ) );
		Assert.IsType<TarGzBundleType>( registry.Create( "Tar.GZ" ) );
		Assert.True( registry.TryCreate( "directory", out _ ) );
		Assert.False( registry.TryCreate( "rar", out _ ) );

		UnknownBundleTypeException unknown =
			Assert.Throws<UnknownBundleTypeException>( () => registry.Create( "rar" ) );
		Assert.Equal( "rar", unknown.Name );
		Assert.Contains( "zip", unknown.Message );
	}

	[Fact]
	public void RegistryAcceptsCustomRegistrations()
	{
		BundleTypeRegistry registry = new ();
		registry.RegisterBundleTypes();

		registry.Register( "custom", () => new CustomType() );

		Assert.IsType<CustomType>( registry.Create( "custom" ) );
		Assert.Contains( "custom", registry.Names );
	}

	private static string TestDirectory()
	{
		string root = Path.Combine( Path.GetTempPath(), "sqetch-bundling-tests", Guid.NewGuid().ToString( "N" ) );

		Directory.CreateDirectory( root );

		return root;
	}

	private sealed class CustomType : IBundleType
	{
		public string Name => "custom";

		public string DefaultOutputName( string baseName ) => baseName;

		public IBundleWriter Create( string outputPath ) => throw new NotSupportedException();

		public IBundleReader Open( string path ) => throw new NotSupportedException();
	}
}