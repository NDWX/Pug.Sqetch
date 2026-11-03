using Pug.Sqetch.Deployment.Bundling;
using Pug.Sqetch.DatabaseDriver;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Deployment.Cli.Commands;

public sealed class DeployCommand(
	IAnsiConsole console,
	IDatabaseDriverRegistry drivers,
	IBundleTypeRegistry bundleTypes,
	IBundleLayoutRegistry layouts
)
	: Command<DeploymentSettings>
{
	protected override int Execute(
		CommandContext context,
		DeploymentSettings settings,
		CancellationToken cancellationToken
	)
	{
		IBundleType type = DeploymentTarget.ResolveBundleType( bundleTypes, settings.Bundle, settings.BundleType );

		using IBundleReader reader = type.Open( settings.Bundle );

		// validation reads the metadata, resolves the layout it names, and confirms the manifest, the
		// journaling statements and every listed script before anything touches the database
		ValidatedBundle validated = BundleValidator.Validate( reader, layouts );

		IDatabaseDriver driver =
			DeploymentTarget.CreateDriver( drivers, settings.Driver!, context.Remaining, settings.StepScriptTimeout );

		// the driver owns the connection it opened, and this command is the only thing that knows
		// the deployment is over — including the failure paths below, which still rethrow
		using IDatabaseDriver owned = driver;

		using ConsoleDeploymentListener listener = new( console, settings.Log );

		listener.BundleRead( validated.Metadata );

		if( settings.NoVerify )
			listener.VerificationSkipped();

		try
		{
			new DeploymentEngine(
					driver,
					validated.Journaling,
					new BundleScriptContentReader( reader, validated.Layout ),
					settings.EffectiveCommitLevel,
					listener,
					settings.RollbackTrigger
				)
				.Deploy( validated.Manifest.Project.Name, validated.Releases, verifySteps: !settings.NoVerify );
		}
		catch( Exception exception )
		{
			listener.Failed( DeployCliErrors.Describe( exception ) );
			DeployCliErrors.Report( console, exception );

			// reported here rather than rethrown to the central handler because only this scope knows
			// whether anything committed, and that is half of what the exit code says
			return DeployExitCodes.For( exception, listener.DatabaseChanged );
		}

		console.WriteLine( listener.UpToDate ? "database is up to date" : "deployment complete" );

		return DeployExitCodes.Success;
	}
}
