using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Pug.Sqetch.Bundling;
using Pug.Sqetch.Bundling.BundleTypes;
using Pug.Sqetch.Bundling.Layouts;
using Pug.Sqetch.Models;

namespace Pug.Sqetch.Tests.Bundling;

public class BundlePackagerTests
{
	private static readonly DateTime Bundled = new ( 2026, 10, 17, 3, 15, 42, DateTimeKind.Utc );

	[Fact]
	public void MetadataIsWrittenBeforeAnythingTheLayoutWrites()
	{
		RecordingWriter writer = new ();

		BundleMetadata metadata = BundlePackager.Write(
			Sample(), new DefaultBundleLayout(), writer, "sqetch-deploy", "0.1.0", Bundled );

		Assert.Equal( "META-DATA", writer.Paths[0] );
		Assert.Equal( "manifest.json", writer.Paths[1] );
		Assert.Single( writer.Paths, x => x == BundleMetadata.EntryName );
		Assert.Equal( metadata, BundleMetadata.Parse( writer.Contents[0] ) );
	}

	[Fact]
	public void FormatAndLayoutComeFromTheLayoutAndTheRestFromTheCaller()
	{
		FormatTwoLayout layout = new ();

		BundleMetadata metadata = BundlePackager.Write(
			Sample(), layout, new RecordingWriter(), "tool", "9.9.9-pre.1", Bundled );

		Assert.Equal( new BundleMetadata( 2, "format-two", "tool", "9.9.9-pre.1", Bundled ), metadata );
		Assert.True( layout.Written );
	}

	[Fact]
	public void ANonUtcInstantIsRefusedBeforeTheLayoutWritesAnything()
	{
		FormatTwoLayout layout = new ();
		RecordingWriter writer = new ();

		Assert.Throws<InvalidBundleMetadataException>(
			() => BundlePackager.Write(
				Sample(), layout, writer, "tool", "1.0.0", DateTime.SpecifyKind( Bundled, DateTimeKind.Local ) ) );

		Assert.Empty( writer.Paths );
		Assert.False( layout.Written );
	}

	[Fact]
	public void TheManifestCarriesNoGeneratedTimestamp()
	{
		RecordingWriter writer = new ();

		BundlePackager.Write( Sample(), new DefaultBundleLayout(), writer, "sqetch-deploy", "0.1.0", Bundled );

		using JsonDocument manifest = JsonDocument.Parse( writer.Contents[writer.Paths.IndexOf( "manifest.json" )] );

		Assert.False( manifest.RootElement.TryGetProperty( "generated", out _ ) );
	}

	[Fact]
	public void ZipHoldsTheMetadataFirst()
	{
		using MemoryStream output = Package( new ZipBundleType().Create );

		using ZipArchive archive = new ( output, ZipArchiveMode.Read );

		Assert.Equal( "META-DATA", archive.Entries[0].FullName );
		Assert.Equal( "manifest.json", archive.Entries[1].FullName );
		Assert.Equal( "format=1", ReadAll( archive.Entries[0].Open() ).Split( '\n' )[0] );
	}

	[Fact]
	public void TarHoldsTheMetadataFirst()
	{
		using MemoryStream output = Package( new TarBundleType().Create );

		using TarReader reader = new ( output );

		TarEntry first = reader.GetNextEntry()!;

		Assert.Equal( "META-DATA", first.Name );
		Assert.Equal( "layout=default", ReadAll( first.DataStream! ).Split( '\n' )[1] );
		Assert.Equal( "manifest.json", reader.GetNextEntry()!.Name );
	}

	[Fact]
	public void TarGzHoldsTheMetadataFirst()
	{
		using MemoryStream output = Package( new TarGzBundleType().Create );

		using GZipStream gzip = new ( output, CompressionMode.Decompress );
		using TarReader reader = new ( gzip );

		Assert.Equal( "META-DATA", reader.GetNextEntry()!.Name );
		Assert.Equal( "manifest.json", reader.GetNextEntry()!.Name );
	}

	[Fact]
	public void DirectoryHoldsTheMetadata()
	{
		string root = Path.Combine( Path.GetTempPath(), "sqetch-packager-tests", Guid.NewGuid().ToString( "N" ) );
		string path = Path.Combine( root, "bundle" );

		Directory.CreateDirectory( root );

		using( IBundleWriter writer = new DirectoryBundleType().Create( path ) )
			BundlePackager.Write( Sample(), new DefaultBundleLayout(), writer, "sqetch-deploy", "0.1.0", Bundled );

		string text = File.ReadAllText( Path.Combine( path, "META-DATA" ) );

		Assert.Equal( new BundleMetadata( 1, "default", "sqetch-deploy", "0.1.0", Bundled ), BundleMetadata.Parse( text ) );
	}

	private static MemoryStream Package( Func<Stream, IBundleWriter> create )
	{
		MemoryStream output = new ();

		using( IBundleWriter writer = create( output ) )
			BundlePackager.Write( Sample(), new DefaultBundleLayout(), writer, "sqetch-deploy", "0.1.0", Bundled );

		output.Position = 0;

		return output;
	}

	private static string ReadAll( Stream stream )
	{
		using( stream )
		using( StreamReader reader = new ( stream ) )
			return reader.ReadToEnd();
	}

	private static Bundle Sample()
		=> new (
			new ProjectDefinition( "demo", "", "postgres" ),
			BundleSelection.Finalized,
			new JournalingStatements( JournalingSlots.All.ToDictionary( slot => slot, slot => (string?) $"select '{slot}'" ) ),
			[new BundleRelease( "2026.07", "", "", true )],
			[
				new BundlePlan(
					"table", "", "2026.07", [],
					[
						new BundleStep(
							"create", "", [],
							() => new StepScripts(
								new MemoryStream( Encoding.UTF8.GetBytes( "-- d" ) ),
								new MemoryStream( Encoding.UTF8.GetBytes( "-- v" ) ),
								new MemoryStream( Encoding.UTF8.GetBytes( "-- r" ) ) ) )
					] )
			] );

	private sealed class RecordingWriter : IBundleWriter
	{
		public List<string> Paths { get; } = [];

		public List<string> Contents { get; } = [];

		public void Add( string path, Stream content )
		{
			using MemoryStream buffer = new ();

			content.CopyTo( buffer );

			Paths.Add( path );
			Contents.Add( Encoding.UTF8.GetString( buffer.ToArray() ) );
		}

		public void Dispose()
		{
		}
	}

	/// <summary>A layout of a higher format, whose only job is to be written through.</summary>
	private sealed class FormatTwoLayout : IBundleLayout
	{
		public bool Written { get; private set; }

		public string Name => "format-two";

		public int Format => 2;

		public void Write( Bundle bundle, IBundleWriter writer ) => Written = true;

		public BundleManifest ReadManifest( IBundleReader reader ) => throw new NotSupportedException();

		public JournalingStatements ReadJournalingStatements( IBundleReader reader ) => throw new NotSupportedException();

		public string ScriptPath( string plan, string step, StepScriptKind kind ) => throw new NotSupportedException();
	}
}
