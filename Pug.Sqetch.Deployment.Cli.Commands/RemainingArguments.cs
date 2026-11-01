using Spectre.Console.Cli;

namespace Pug.Sqetch.Deployment.Cli.Commands;

internal static class RemainingArguments
{
	/// <summary>
	/// Refuses anything the command tree did not recognize. Strict parsing is off app-wide so the
	/// deploy command can pick a driver's '--&lt;driver&gt;-&lt;parameter&gt;' switches out of the
	/// leftovers; every other command must therefore refuse them itself, or a typo'd option would
	/// be swallowed in silence and the command would report success.
	/// </summary>
	public static void RejectAll( IRemainingArguments remaining )
	{
		if( remaining.Parsed.Count > 0 )
			throw new CommandLineException( $"unknown option '{remaining.Parsed.First().Key}'" );

		if( remaining.Raw.Count > 0 )
			throw new CommandLineException( $"unexpected argument(s): {string.Join( " ", remaining.Raw )}" );
	}
}
