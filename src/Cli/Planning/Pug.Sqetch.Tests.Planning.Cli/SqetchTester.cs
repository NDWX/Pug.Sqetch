using Pug.Sqetch.Cli;
using Pug.Sqetch.Planning.Cli;

using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;

namespace Pug.Sqetch.Tests.Planning.Cli;

/// <summary>
/// The real 'sqetch' composition in a <see cref="CommandAppTester"/>, with two test seams: the
/// user-level template root, which defaults to a folder that never exists so a developer's own
/// templates cannot leak into a test, and the warning console, which is captured rather than
/// written to the process's standard error.
/// </summary>
internal static class SqetchTester
{
	private static readonly string NoUserTemplates =
		Path.Combine( Path.GetTempPath(), "sqetch-no-user-templates", Guid.NewGuid().ToString( "N" ) );

	public static CommandAppTester Create(
		string? userTemplateRoot = null,
		TestConsole? console = null,
		TestConsole? warnings = null )
	{
		TypeRegistrar registrar = SqetchApp.CreateDefaultRegistrar( userTemplateRoot ?? NoUserTemplates );

		registrar.RegisterInstance( typeof(WarningWriter), new WarningWriter( warnings ?? new TestConsole() ) );

		CommandAppTester tester = new( registrar, console: console );

		tester.Configure( SqetchApp.Configure );

		return tester;
	}
}
