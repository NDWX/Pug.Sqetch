namespace Pug.Sqetch.Cli;

public enum OutputFormat
{
	/// <summary>Table on an interactive console, tab-separated rows when piped/redirected.</summary>
	Auto,
	Json,
	Csv
}

public static class OutputFormats
{
	public static bool TryParse( string? value, out OutputFormat format )
	{
		switch( value?.ToLowerInvariant() )
		{
			case null or "":
				format = OutputFormat.Auto;
				return true;

			case "json":
				format = OutputFormat.Json;
				return true;

			case "csv":
				format = OutputFormat.Csv;
				return true;

			default:
				format = OutputFormat.Auto;
				return false;
		}
	}
}
