using Spectre.Console;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Cli;

/// <summary>Base settings for commands producing complex results: the '--output' switch.</summary>
public class OutputSettings : CommandSettings
{
	[CommandOption( "-o|--output <FORMAT>" )]
	public string? Output { get; init; }

	public OutputFormat Format { get; private set; }

	public override ValidationResult Validate()
	{
		if( !OutputFormats.TryParse( Output, out OutputFormat format ) )
			return ValidationResult.Error( "--output must be 'json' or 'csv'" );

		Format = format;

		return ValidationResult.Success();
	}
}
