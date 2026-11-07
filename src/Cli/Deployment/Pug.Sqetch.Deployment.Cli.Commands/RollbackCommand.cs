using System.ComponentModel;

using Pug.Sqetch.Cli;
using Pug.Sqetch.Deployment.Bundling;
using Pug.Sqetch.DatabaseDriver;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Deployment.Cli.Commands;

public sealed class RollbackSettings : DatabaseTargetSettings
{
	[CommandOption( "--to <RELEASE>" )]
	[Description( "Release to leave in place; everything deployed after it is undone" )]
	public string? To { get; init; }

	[CommandOption( "--all" )]
	[Description( "Undo everything deployed" )]
	public bool All { get; init; }

	[CommandOption( "--plans <LIST>" )]
	[Description(
		"Plans of the release the database is at to undo, comma- or semicolon-separated; " +
		"plans depending on them must be listed too"
	)]
	public string? Plans { get; init; }

	internal RollbackTarget Target { get; private set; } = RollbackTarget.All;

	public override ValidationResult Validate()
	{
		ValidationResult shared = base.Validate();

		if( !shared.Successful )
			return shared;

		int given = ( To is not null ? 1 : 0 ) + ( All ? 1 : 0 ) + ( Plans is not null ? 1 : 0 );

		if( given != 1 )
			return ValidationResult.Error( "specify exactly one of --to <release>, --all or --plans <list>" );

		if( To is not null )
		{
			if( To.Length == 0 )
				return ValidationResult.Error( "--to must name a release" );

			Target = RollbackTarget.ToRelease( To );
		}
		else if( All )
			Target = RollbackTarget.All;
		else
		{
			string[] plans = OptionLists.Split( Plans );

			if( plans.Length == 0 )
				return ValidationResult.Error( "--plans must name at least one plan" );

			Target = RollbackTarget.OfPlans( plans );
		}

		return ValidationResult.Success();
	}
}

/// <summary>
/// Undoes what a database has deployed, by running the bundle's rollback scripts in reverse and
/// journaling through the RollingBack*/RolledBack* slots: back to a named release (<c>--to</c>),
/// everything (<c>--all</c>) or named plans of the latest release (<c>--plans</c>). The bundle
/// must hold what is undone; a target that does not fit the database is refused before anything runs.
/// </summary>
public sealed class RollbackCommand(
	IAnsiConsole console,
	IDatabaseDriverRegistry drivers,
	IBundleTypeRegistry bundleTypes,
	IBundleLayoutRegistry layouts,
	WarningWriter warnings
)
	: Command<RollbackSettings>
{
	protected override int Execute(
		CommandContext context,
		RollbackSettings settings,
		CancellationToken cancellationToken
	)
	{
		IBundleType type = DeploymentTarget.ResolveBundleType( bundleTypes, settings.Bundle, settings.BundleType );

		using IBundleReader reader = type.Open( settings.Bundle );

		ValidatedBundle validated = BundleValidator.Validate( reader, layouts );

		IDatabaseDriver driver =
			DeploymentTarget.CreateDriver( drivers, settings.Driver!, context.Remaining, settings.StepScriptTimeout );

		using IDatabaseDriver owned = driver;

		using ConsoleDeploymentListener listener = new( console, settings.Log );

		listener.BundleRead( validated.Metadata );

		listener.WarnWhenRollingBack(
			validated.Manifest.Releases.Where( x => !x.Finalized )
				.Select( x => x.Name )
				.Concat( validated.Manifest.Plans.Any( x => x.Release.Length == 0 ) ? [ "" ] : [ ] ),
			warnings
		);

		try
		{
			new DeploymentEngine(
					driver,
					validated.Journaling,
					new BundleScriptContentReader( reader, validated.Layout ),
					settings.EffectiveCommitLevel,
					listener,
					RollbackTrigger.None
				)
				.RollBack( validated.Manifest.Project.Name, validated.Releases, settings.Target );
		}
		catch( Exception exception )
		{
			listener.Failed( DeployCliErrors.Describe( exception ) );
			DeployCliErrors.Report( console, exception );

			return DeployExitCodes.For( exception );
		}

		console.WriteLine( listener.NothingRolledBack ? "database unchanged" : "rollback complete" );

		return DeployExitCodes.Success;
	}
}
