using System.Text;
using Pug.Sqetch.Bundling;

namespace Pug.Sqetch.Tests.Bundling;

public class BundleMetadataTests
{
	private static readonly DateTime Bundled = new ( 2026, 10, 17, 3, 15, 42, DateTimeKind.Utc );

	private const string Valid =
		"format=1\nlayout=default\nbundler=sqetch-deploy\nbundler-version=0.1.0\nbundled=2026-10-17T03:15:42Z\n";

	[Fact]
	public void ToTextWritesTheFiveKeysInOrderWithLfEndingsAndNoByteOrderMark()
	{
		BundleMetadata metadata = new ( 1, "default", "sqetch-deploy", "0.1.0", Bundled );

		Assert.Equal( Valid, metadata.ToText() );
		Assert.DoesNotContain( '\r', metadata.ToText() );
		Assert.NotEqual( '﻿', metadata.ToText()[0] );
	}

	[Fact]
	public void ParseReadsWhatToTextWrote()
	{
		BundleMetadata metadata = new ( 3, "default", "sqetch-deploy", "0.1.0+abc123", Bundled );

		BundleMetadata parsed = BundleMetadata.Parse( metadata.ToText() );

		Assert.Equal( metadata, parsed );
		Assert.Equal( DateTimeKind.Utc, parsed.Bundled.Kind );
	}

	[Fact]
	public void CommentsBlankLinesWhitespaceAndUnknownKeysAreIgnored()
	{
		BundleMetadata parsed = BundleMetadata.Parse(
			"# written by hand\n\n  format = 1  \n\t# another\nlayout=default\nfuture-key=whatever\n"
			+ "future-key=again\nbundler =sqetch-deploy\n   \nbundler-version= 0.1.0\nbundled=2026-10-17T03:15:42Z" );

		Assert.Equal( new BundleMetadata( 1, "default", "sqetch-deploy", "0.1.0", Bundled ), parsed );
	}

	[Fact]
	public void ValuesAreSplitAtTheFirstEqualsSign()
	{
		BundleMetadata parsed = BundleMetadata.Parse( Valid.Replace( "bundler=sqetch-deploy", "bundler=a=b=c" ) );

		Assert.Equal( "a=b=c", parsed.Bundler );
	}

	[Fact]
	public void CrlfLineEndingsAreAccepted()
	{
		Assert.Equal(
			new BundleMetadata( 1, "default", "sqetch-deploy", "0.1.0", Bundled ),
			BundleMetadata.Parse( Valid.Replace( "\n", "\r\n" ) ) );
	}

	[Fact]
	public void ALeadingByteOrderMarkIsTolerated()
	{
		Assert.Equal( 1, BundleMetadata.Parse( "﻿" + Valid ).Format );

		InMemoryReader reader = new ( BundleMetadata.EntryName, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes( Valid )] );

		Assert.Equal( 1, BundleMetadata.Read( reader ).Format );
	}

	[Theory]
	[InlineData( "format" )]
	[InlineData( "layout" )]
	[InlineData( "bundler" )]
	[InlineData( "bundler-version" )]
	[InlineData( "bundled" )]
	public void EveryKeyIsRequired( string key )
	{
		string without = string.Join( "\n", Valid.Split( '\n' ).Where( x => !x.StartsWith( key + "=" ) ) );

		InvalidBundleMetadataException error = Assert.Throws<InvalidBundleMetadataException>(
			() => BundleMetadata.Parse( without ) );

		Assert.Contains( $"has no '{key}'", error.Message );
	}

	[Theory]
	[InlineData( "layout" )]
	[InlineData( "bundler" )]
	[InlineData( "bundler-version" )]
	public void TextValuesMustNotBeEmpty( string key )
	{
		string empty = string.Join(
			"\n", Valid.Split( '\n' ).Select( x => x.StartsWith( key + "=" ) ? key + "=" : x ) );

		InvalidBundleMetadataException error = Assert.Throws<InvalidBundleMetadataException>(
			() => BundleMetadata.Parse( empty ) );

		Assert.Contains( key, error.Message );
	}

	[Fact]
	public void ADuplicateKeyIsInvalid()
	{
		InvalidBundleMetadataException error = Assert.Throws<InvalidBundleMetadataException>(
			() => BundleMetadata.Parse( Valid + "layout=other\n" ) );

		Assert.Contains( "'layout' more than once", error.Message );
		Assert.Contains( "line 6", error.Message );
	}

	[Fact]
	public void ALineWithoutAnEqualsSignIsInvalidAndNamesItsLineNumber()
	{
		InvalidBundleMetadataException error = Assert.Throws<InvalidBundleMetadataException>(
			() => BundleMetadata.Parse( "format=1\n# note\n\nnonsense\n" ) );

		Assert.Contains( "line 4", error.Message );
	}

	[Fact]
	public void ALineWithNoKeyIsInvalid()
	{
		Assert.Throws<InvalidBundleMetadataException>( () => BundleMetadata.Parse( "=1\n" ) );
	}

	[Theory]
	[InlineData( "0" )]
	[InlineData( "-1" )]
	[InlineData( "one" )]
	[InlineData( "1.0" )]
	[InlineData( "+1" )]
	[InlineData( "99999999999" )]
	public void TheFormatMustBeAPositiveWholeNumber( string value )
	{
		InvalidBundleMetadataException error = Assert.Throws<InvalidBundleMetadataException>(
			() => BundleMetadata.Parse( Valid.Replace( "format=1", $"format={value}" ) ) );

		Assert.Contains( "'format'", error.Message );
	}

	[Theory]
	[InlineData( "2026-10-17T03:15:42" )]
	[InlineData( "2026-10-17T03:15:42+02:00" )]
	[InlineData( "2026-10-17T03:15:42.123Z" )]
	[InlineData( "2026-10-17 03:15:42Z" )]
	[InlineData( "2026-13-17T03:15:42Z" )]
	[InlineData( "2026-02-30T03:15:42Z" )]
	[InlineData( "yesterday" )]
	public void TheBundledInstantMustBeExactlyAUtcInstantToTheSecond( string value )
	{
		InvalidBundleMetadataException error = Assert.Throws<InvalidBundleMetadataException>(
			() => BundleMetadata.Parse( Valid.Replace( "2026-10-17T03:15:42Z", value ) ) );

		Assert.Contains( "'bundled'", error.Message );
	}

	[Theory]
	[InlineData( DateTimeKind.Local )]
	[InlineData( DateTimeKind.Unspecified )]
	public void ANonUtcBundledInstantIsRefusedOnWrite( DateTimeKind kind )
	{
		BundleMetadata metadata = new ( 1, "default", "sqetch-deploy", "0.1.0", DateTime.SpecifyKind( Bundled, kind ) );

		InvalidBundleMetadataException error = Assert.Throws<InvalidBundleMetadataException>( metadata.ToText );

		Assert.Contains( "UTC", error.Message );
		Assert.Throws<InvalidBundleMetadataException>( () => metadata.Write( new RecordingWriter() ) );
	}

	[Theory]
	[InlineData( 0 )]
	[InlineData( -1 )]
	public void ANonPositiveFormatIsRefusedOnWrite( int format )
	{
		BundleMetadata metadata = new ( format, "default", "sqetch-deploy", "0.1.0", Bundled );

		Assert.Throws<InvalidBundleMetadataException>( metadata.ToText );
	}

	[Fact]
	public void ValuesThatWouldSpanLinesOrBeBlankAreRefusedOnWrite()
	{
		Assert.Throws<InvalidBundleMetadataException>(
			() => new BundleMetadata( 1, "default", "sqetch\ndeploy", "0.1.0", Bundled ).ToText() );
		Assert.Throws<InvalidBundleMetadataException>(
			() => new BundleMetadata( 1, "", "sqetch-deploy", "0.1.0", Bundled ).ToText() );
	}

	[Fact]
	public void WriteAddsTheTextAsTheMetadataEntry()
	{
		RecordingWriter writer = new ();

		new BundleMetadata( 1, "default", "sqetch-deploy", "0.1.0", Bundled ).Write( writer );

		Assert.Equal( "META-DATA", BundleMetadata.EntryName );
		Assert.Equal( [(BundleMetadata.EntryName, Valid)], writer.Entries );
	}

	[Fact]
	public void ReadParsesTheMetadataEntry()
	{
		InMemoryReader reader = new ( BundleMetadata.EntryName, Encoding.UTF8.GetBytes( Valid ) );

		Assert.Equal(
			new BundleMetadata( 1, "default", "sqetch-deploy", "0.1.0", Bundled ),
			BundleMetadata.Read( reader ) );
	}

	[Fact]
	public void ReadRefusesABundleWithoutTheEntryOrWithADifferentlyCasedOne()
	{
		InvalidBundleMetadataException missing = Assert.Throws<InvalidBundleMetadataException>(
			() => BundleMetadata.Read( new InMemoryReader( "other", [] ) ) );

		Assert.Contains( "META-DATA", missing.Message );

		Assert.Throws<InvalidBundleMetadataException>(
			() => BundleMetadata.Read( new InMemoryReader( "meta-data", Encoding.UTF8.GetBytes( Valid ) ) ) );
	}

	[Fact]
	public void ReadRefusesBytesThatAreNotUtf8()
	{
		InvalidBundleMetadataException error = Assert.Throws<InvalidBundleMetadataException>(
			() => BundleMetadata.Read( new InMemoryReader( BundleMetadata.EntryName, [0xFF, 0xFE, 0xFD, 0x80] ) ) );

		Assert.Contains( "UTF-8", error.Message );
	}

	private sealed class InMemoryReader( string path, byte[] content ) : IBundleReader
	{
		public IEnumerable<string> Entries => [path];

		public bool Contains( string candidate ) => candidate == path;

		public Stream Open( string candidate )
			=> candidate == path ? new MemoryStream( content ) : throw new BundlingException( $"no {candidate}" );

		public void Dispose()
		{
		}
	}

	private sealed class RecordingWriter : IBundleWriter
	{
		public List<(string Path, string Content)> Entries { get; } = [];

		public void Add( string path, Stream content )
		{
			using MemoryStream buffer = new ();

			content.CopyTo( buffer );

			Entries.Add( (path, Encoding.UTF8.GetString( buffer.ToArray() )) );
		}

		public void Dispose()
		{
		}
	}
}
