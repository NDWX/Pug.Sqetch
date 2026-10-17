using System.Globalization;
using System.Text;

namespace Pug.Sqetch.Bundling;

/// <summary>
/// What a bundle says about itself, kept in an entry of its own (<see cref="EntryName"/>) and apart
/// from the manifest: it is what a reader consults before deciding whether it can read the rest at
/// all, so its syntax must outlive any change to a layout's manifest schema. <paramref name="Format"/>
/// is the version of the layout named by <paramref name="Layout"/>; <paramref name="Bundler"/> and
/// <paramref name="BundlerVersion"/> identify the tool that built the bundle; <paramref name="Bundled"/>
/// is the UTC instant it was built, to the whole second.
///
/// The syntax is UTF-8 text, one <c>key=value</c> per line, split at the first '=' with key and value
/// trimmed. Blank lines and lines starting with '#' are ignored, and so are unknown keys, so a later
/// writer may add one without breaking this reader; a known key given twice, and a line with no '=',
/// are invalid. All five keys are required. <see cref="Parse"/> and <see cref="ToText"/> are the only
/// code that knows any of this.
/// </summary>
public sealed record BundleMetadata(
	int Format,
	string Layout,
	string Bundler,
	string BundlerVersion,
	DateTime Bundled )
{
	/// <summary>The entry the metadata lives in; matched exactly.</summary>
	public const string EntryName = "META-DATA";

	private const string FormatKey = "format";
	private const string LayoutKey = "layout";
	private const string BundlerKey = "bundler";
	private const string BundlerVersionKey = "bundler-version";
	private const string BundledKey = "bundled";

	private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

	private static readonly UTF8Encoding StrictUtf8 = new ( encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true );

	/// <summary>Reads and parses the metadata entry; throws <see cref="InvalidBundleMetadataException"/> when it is missing or invalid.</summary>
	public static BundleMetadata Read( IBundleReader reader )
	{
		ArgumentNullException.ThrowIfNull( reader );

		if( !reader.Contains( EntryName ) )
			throw new InvalidBundleMetadataException( $"Bundle has no '{EntryName}' entry." );

		string text;

		try
		{
			using Stream entry = reader.Open( EntryName );

			// no byte-order detection: the entry is UTF-8, so a UTF-16 mark is a malformed entry and not
			// a cue to decode it differently; a UTF-8 mark is left for Parse to tolerate
			using StreamReader content = new ( entry, StrictUtf8, detectEncodingFromByteOrderMarks: false );

			text = content.ReadToEnd();
		}
		catch( DecoderFallbackException )
		{
			throw new InvalidBundleMetadataException( $"Bundle '{EntryName}' is not valid UTF-8 text." );
		}

		return Parse( text );
	}

	/// <summary>Adds the metadata to a bundle being written; see <see cref="ToText"/> for what is refused.</summary>
	public void Write( IBundleWriter writer )
	{
		ArgumentNullException.ThrowIfNull( writer );

		using MemoryStream content = new ( Encoding.UTF8.GetBytes( ToText() ) );

		writer.Add( EntryName, content );
	}

	public static BundleMetadata Parse( string text )
	{
		ArgumentNullException.ThrowIfNull( text );

		Dictionary<string, string> values = new ();

		string[] lines = text.TrimStart( '﻿' ).Split( '\n' );

		for( int index = 0; index < lines.Length; index++ )
		{
			string line = lines[index].Trim();

			if( line.Length == 0 || line.StartsWith( '#' ) )
				continue;

			int separator = line.IndexOf( '=' );
			string key = separator < 0 ? "" : line[..separator].Trim();

			if( key.Length == 0 )
				throw new InvalidBundleMetadataException(
					$"Bundle '{EntryName}' line {index + 1} is not a 'key=value' pair." );

			// unknown keys are skipped whole, repeats included: they belong to a newer writer
			if( IsKnown( key ) && !values.TryAdd( key, line[( separator + 1 )..].Trim() ) )
				throw new InvalidBundleMetadataException(
					$"Bundle '{EntryName}' gives '{key}' more than once (line {index + 1})." );
		}

		return new BundleMetadata(
			ParseFormat( Required( values, FormatKey ) ),
			Required( values, LayoutKey ),
			Required( values, BundlerKey ),
			Required( values, BundlerVersionKey ),
			ParseInstant( Required( values, BundledKey ) ) );
	}

	/// <summary>
	/// The metadata as it is written to a bundle: LF line endings, no byte-order mark, keys in a
	/// fixed order. Throws <see cref="InvalidBundleMetadataException"/> for anything that
	/// <see cref="Parse"/> would not accept back, including a <see cref="Bundled"/> that is not UTC —
	/// converting it would be a guess about what the caller meant.
	/// </summary>
	public string ToText()
	{
		if( Format < 1 )
			throw new InvalidBundleMetadataException( $"Bundle format must be a positive number, not {Format}." );

		RequireSingleLine( LayoutKey, Layout );
		RequireSingleLine( BundlerKey, Bundler );
		RequireSingleLine( BundlerVersionKey, BundlerVersion );

		if( Bundled.Kind != DateTimeKind.Utc )
			throw new InvalidBundleMetadataException(
				$"Bundled instant must be UTC, not {Bundled.Kind}; convert it before writing." );

		return string.Join(
			'\n',
			$"{FormatKey}={Format.ToString( CultureInfo.InvariantCulture )}",
			$"{LayoutKey}={Layout}",
			$"{BundlerKey}={Bundler}",
			$"{BundlerVersionKey}={BundlerVersion}",
			$"{BundledKey}={Bundled.ToString( InstantFormat, CultureInfo.InvariantCulture )}" ) + "\n";
	}

	private static bool IsKnown( string key )
		=> key is FormatKey or LayoutKey or BundlerKey or BundlerVersionKey or BundledKey;

	private static string Required( Dictionary<string, string> values, string key )
	{
		if( !values.TryGetValue( key, out string? value ) )
			throw new InvalidBundleMetadataException( $"Bundle '{EntryName}' has no '{key}'." );

		if( value.Length == 0 )
			throw new InvalidBundleMetadataException( $"Bundle '{EntryName}' gives '{key}' no value." );

		return value;
	}

	private static int ParseFormat( string value )
	{
		if( !int.TryParse( value, NumberStyles.None, CultureInfo.InvariantCulture, out int format ) || format < 1 )
			throw new InvalidBundleMetadataException(
				$"Bundle '{EntryName}' gives '{FormatKey}' as '{value}', which is not a positive whole number." );

		return format;
	}

	private static DateTime ParseInstant( string value )
	{
		// only the one spelling: no offset, no fraction, no local time to be guessed at
		if( !DateTime.TryParseExact(
				value, InstantFormat, CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime instant ) )
			throw new InvalidBundleMetadataException(
				$"Bundle '{EntryName}' gives '{BundledKey}' as '{value}', which is not a UTC instant"
				+ " written as yyyy-MM-ddTHH:mm:ssZ." );

		return instant;
	}

	private static void RequireSingleLine( string key, string value )
	{
		if( string.IsNullOrWhiteSpace( value ) )
			throw new InvalidBundleMetadataException( $"Bundle '{key}' must not be empty." );

		if( value.Contains( '\n' ) || value.Contains( '\r' ) )
			throw new InvalidBundleMetadataException( $"Bundle '{key}' must not span lines." );
	}
}
