using Spectre.Console;

namespace Pug.Sqetch.Cli;

/// <summary>
/// Where a command reports something worth knowing that does not stop it. Warnings go to standard
/// error, so whatever a command prints on standard output stays clean for a script reading it; the
/// host registers <see cref="ToStandardError"/>, and tests register one over a test console.
/// </summary>
public sealed class WarningWriter( IAnsiConsole console )
{
	public static WarningWriter ToStandardError()
		=> new( AnsiConsole.Create( new AnsiConsoleSettings { Out = new AnsiConsoleOutput( Console.Error ) } ) );

	public void Write( string message )
		=> console.MarkupLineInterpolated( $"[yellow]warning:[/] {message}" );
}
