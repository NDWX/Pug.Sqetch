using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Deployment.Cli.Commands;

public sealed class RollbackSettings : DeploymentSettings
{
	[CommandOption( "--after-release <NAME>" )]
	[Description( "Roll back everything deployed after this release" )]
	public string? AfterRelease { get; init; }

	[CommandOption( "--until-release <NAME>" )]
	[Description( "Roll back down to and including this release" )]
	public string? UntilRelease { get; init; }

	public override ValidationResult Validate()
	{
		if( AfterRelease is not null && UntilRelease is not null )
			return ValidationResult.Error( "specify either --after-release or --until-release, not both" );

		if( AfterRelease is null && UntilRelease is null )
			return ValidationResult.Error( "rollback requires --after-release or --until-release" );

		return base.Validate();
	}
}

/// <summary>
/// Carries the full rollback switch surface already, but the action itself is not
/// implemented yet.
/// </summary>
public sealed class RollbackCommand : Command<RollbackSettings>
{
	protected override int Execute( CommandContext context, RollbackSettings settings, CancellationToken cancellationToken )
		=> throw new OperationNotSupportedException( "rollback is not yet supported" );
}
