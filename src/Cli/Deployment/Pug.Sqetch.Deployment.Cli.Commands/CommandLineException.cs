namespace Pug.Sqetch.Deployment.Cli.Commands;

/// <summary>
/// The command line itself is wrong, as judged by the checks the command tree cannot make for
/// itself: an option outside the selected driver's prefix, a parameter that driver does not define,
/// a missing or repeated value, an unsatisfied required parameter set. Its own type so the exit
/// code can say "nothing was read and nothing was touched" without matching on message text.
/// </summary>
internal sealed class CommandLineException
	: DeploymentException
{
	public CommandLineException( string message )
		: base( message )
	{
	}
}