using System.Globalization;

using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Cli.Commands.Authoring;

internal static class OptionParsing
{
	/// <summary>Splits a comma- or semicolon-separated option value.</summary>
	public static string[] SplitList( string? value )
		=> OptionLists.Split( value );

	public static bool TryParseDate( string? value, out DateTime? date )
	{
		date = null;

		if( value is null )
			return true;

		if( !DateTime.TryParseExact(
				value,
				"yyyy-MM-dd",
				CultureInfo.InvariantCulture,
				DateTimeStyles.None,
				out DateTime parsed
			) )
			return false;

		date = parsed;

		return true;
	}

	/// <summary>
	/// Half-open day-boundary window: 'after' includes the given day onward (≥ 00:00 of
	/// that day), 'before' excludes the given day (&lt; 00:00 of that day).
	/// </summary>
	public static Range<DateTime>? Window( DateTime? after, DateTime? before )
		=> after is null && before is null ?
			null :
			new Range<DateTime>
			{
				Start = after ?? DateTime.MinValue,
				End = before?.AddTicks( -1 ) ?? DateTime.MaxValue
			};
}