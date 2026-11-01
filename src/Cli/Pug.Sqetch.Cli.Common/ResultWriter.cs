using System.Text;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pug.Sqetch.Cli;

/// <summary>
/// Renders complex command results. Scalars are printed directly by commands; anything
/// tabular goes through here so every list honors the same '--output' contract: JSON and
/// CSV on request, otherwise a table on an interactive console and header-less
/// tab-separated rows when output is piped or redirected.
/// </summary>
public static class ResultWriter
{
	private static readonly JsonSerializerOptions JsonOptions = new ()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		NewLine = "\n"
	};

	public static void WriteRows<T>(
		IAnsiConsole console, OutputFormat format,
		string[] headers, IReadOnlyList<T> items, Func<T, string[]> row, Func<T, object> json )
	{
		switch( format )
		{
			case OutputFormat.Json:
				WriteJson( console, items.Select( json ) );
				break;

			case OutputFormat.Csv:
				WriteLines( console, [ToCsvLine( headers ), ..items.Select( x => ToCsvLine( row( x ) ) )] );
				break;

			case OutputFormat.Auto when console.Profile.Capabilities.Interactive:
				console.Write( BuildTable( headers, items.Select( row ) ) );
				break;

			default:
				WriteLines( console, items.Select( x => string.Join( '\t', row( x ) ) ).ToList() );
				break;
		}
	}

	/// <summary>
	/// A result made of several tables — a set of groupings and then the things grouped, say. The
	/// machine formats stay single documents: JSON serializes <paramref name="json"/> once, so the
	/// whole result is one object rather than one per section, while CSV, being a rectangle by
	/// definition, becomes one block per section separated by a blank line and introduced by the
	/// section's key. Piped output prefixes every row with that key, since header-less rows from two
	/// sections would otherwise be indistinguishable.
	/// </summary>
	public static void WriteSections(
		IAnsiConsole console, OutputFormat format, object json, IReadOnlyList<ResultSection> sections )
	{
		switch( format )
		{
			case OutputFormat.Json:
				WriteJson( console, json );
				break;

			case OutputFormat.Csv:
				List<string> lines = [];

				foreach( ResultSection section in sections )
				{
					if( lines.Count > 0 )
						lines.Add( "" );

					lines.Add( ToCsvLine( [section.Key] ) );
					lines.Add( ToCsvLine( section.Headers ) );
					lines.AddRange( section.Rows.Select( ToCsvLine ) );
				}

				WriteLines( console, lines );
				break;

			case OutputFormat.Auto when console.Profile.Capabilities.Interactive:
				foreach( ResultSection section in sections )
				{
					Table table = BuildTable( section.Headers, section.Rows );

					table.Title = new TableTitle( section.Heading, new Style( decoration: Decoration.Bold ) );

					console.Write( table );
				}

				break;

			default:
				WriteLines(
					console,
					sections
						.SelectMany( section => section.Rows.Select( row => $"{section.Key}\t{string.Join( '\t', row )}" ) )
						.ToList() );
				break;
		}
	}

	/// <summary>
	/// Single-item detail view: a label/value table (or 'label\tvalue' lines when piped);
	/// JSON serializes <paramref name="details"/> itself, CSV emits one header + one row.
	/// </summary>
	public static void WriteDetails(
		IAnsiConsole console, OutputFormat format,
		object details, (string Label, string Value)[] pairs )
	{
		switch( format )
		{
			case OutputFormat.Json:
				WriteJson( console, details );
				break;

			case OutputFormat.Csv:
				WriteLines(
					console,
					[
						ToCsvLine( pairs.Select( x => x.Label ).ToArray() ),
						ToCsvLine( pairs.Select( x => x.Value ).ToArray() )
					] );
				break;

			case OutputFormat.Auto when console.Profile.Capabilities.Interactive:
				Table table = new Table().HideHeaders().AddColumns( "", "" );

				foreach( (string label, string value) in pairs )
					table.AddRow( new Text( label, new Style( decoration: Decoration.Bold ) ), new Text( value ) );

				console.Write( table );
				break;

			default:
				WriteLines( console, pairs.Select( x => $"{x.Label}\t{x.Value}" ).ToList() );
				break;
		}
	}

	private static void WriteJson( IAnsiConsole console, object value )
		=> WriteLines( console, JsonSerializer.Serialize( value, JsonOptions ).Split( '\n' ) );

	/// <summary>
	/// Writes the machine formats a line at a time with wrapping out of the way. Spectre wraps to
	/// the profile's width, which is 80 whenever output is not a terminal — exactly when these
	/// formats are used — and a wrapped line is a corrupt record rather than an ugly one: a newline
	/// lands inside a tab-separated field, inside a quoted CSV cell, or inside a JSON string, which
	/// is an invalid control character there. There is no switch for disabling it, so the width is
	/// widened to the longest line for the duration and put back afterwards; widened to what is
	/// needed rather than to <c>int.MaxValue</c>, since the profile's width is what Spectre sizes
	/// its own buffers from.
	/// </summary>
	private static void WriteLines( IAnsiConsole console, IReadOnlyList<string> lines )
	{
		int width = console.Profile.Width;

		console.Profile.Width = lines.Count == 0 ? width : Math.Max( width, lines.Max( x => x.Length ) + 1 );

		try
		{
			foreach( string line in lines )
				console.WriteLine( line );
		}
		finally
		{
			console.Profile.Width = width;
		}
	}

	private static Table BuildTable( string[] headers, IEnumerable<string[]> rows )
	{
		Table table = new ();

		foreach( string header in headers )
			table.AddColumn( new TableColumn( new Text( header, new Style( decoration: Decoration.Bold ) ) ) );

		foreach( string[] row in rows )
			table.AddRow( row.Select( IRenderable ( x ) => new Text( x ) ).ToArray() );

		return table;
	}

	private static string ToCsvLine( string[] cells )
	{
		StringBuilder line = new ();

		for( int index = 0; index < cells.Length; index++ )
		{
			if( index > 0 )
				line.Append( ',' );

			string cell = cells[index];

			if( cell.Contains( ',' ) || cell.Contains( '"' ) || cell.Contains( '\n' ) )
				line.Append( '"' ).Append( cell.Replace( "\"", "\"\"" ) ).Append( '"' );
			else
				line.Append( cell );
		}

		return line.ToString();
	}
}
