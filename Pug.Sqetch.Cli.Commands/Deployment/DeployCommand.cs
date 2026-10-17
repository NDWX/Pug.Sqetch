using Pug.Sqetch.Bundling;
using Pug.Sqetch.Bundling.BundleTypes;
using Pug.Sqetch.Deployment;
using Pug.Sqetch.DatabaseDriver;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Cli.Commands.Deployment;

public sealed class DeployCommand(
	IAnsiConsole console,
	IDatabaseDriverRegistry drivers,
	IBundleTypeRegistry bundleTypes,
	IBundleLayoutRegistry layouts )
	: Command<DeploymentSettings>
{
	protected override int Execute( CommandContext context, DeploymentSettings settings, CancellationToken cancellationToken )
	{
		IBundleType type = ResolveBundleType( settings );

		using IBundleReader reader = type.Open( settings.Bundle );

		// validation reads the metadata, resolves the layout it names, and confirms the manifest, the
		// journaling statements and every listed script before anything touches the database
		ValidatedBundle validated = BundleValidator.Validate( reader, layouts );

		IDatabaseDriverFactory factory = drivers.Create( settings.Driver! );

		IDictionary<string, string> parameters = DriverParameters.Parse( context.Remaining, factory );

		IDatabaseDriver driver;

		try
		{
			driver = factory.Create( parameters, settings.StepScriptTimeout );
		}
		catch( Exception exception )
		{
			throw new DriverCreationException( factory.Name, exception );
		}

		// the driver owns the connection it opened, and this command is the only thing that knows
		// the deployment is over — including the failure paths below, which still rethrow
		using IDatabaseDriver owned = driver;

		using ConsoleDeploymentListener listener = new ( console, settings.Log );

		listener.BundleRead( validated.Metadata );

		try
		{
			new DeploymentEngine(
					driver, validated.Journaling, reader, validated.Layout, settings.EffectiveCommitLevel, listener,
					settings.RollbackTrigger )
				.Deploy( validated.Manifest );
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

	private IBundleType ResolveBundleType( DeploymentSettings settings )
	{
		if( settings.BundleType is not null )
			return bundleTypes.Create( settings.BundleType );

		if( Directory.Exists( settings.Bundle ) )
			return bundleTypes.Create( DirectoryBundleType.TypeName );

		if( settings.Bundle.EndsWith( ".tar.gz", StringComparison.OrdinalIgnoreCase )
			|| settings.Bundle.EndsWith( ".tgz", StringComparison.OrdinalIgnoreCase ) )
			return bundleTypes.Create( TarGzBundleType.TypeName );

		if( settings.Bundle.EndsWith( ".tar", StringComparison.OrdinalIgnoreCase ) )
			return bundleTypes.Create( TarBundleType.TypeName );

		return bundleTypes.Create( ZipBundleType.TypeName );
	}
}
