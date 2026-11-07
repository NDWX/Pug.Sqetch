namespace Pug.Sqetch.Cli;

/// <summary>Reads list-valued option values, so both CLIs split them the same way.</summary>
public static class OptionLists
{
	/// <summary>Splits a comma- or semicolon-separated option value.</summary>
	public static string[] Split( string? value )
		=> value is null ? [ ] :
			value.Split( [ ',', ';' ], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
}
