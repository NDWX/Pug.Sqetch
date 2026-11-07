using System.ComponentModel;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Deployment.Cli.Commands;

/// <summary>Switches of the deploy command: the shared ones, and how the deployment verifies and undoes itself.</summary>
public class DeploymentSettings : DatabaseTargetSettings
{
	[CommandOption( "--rollback <MODE>" )]
	[Description(
		"Undo the deployment: 'on-error' after a failure, 'on-success' after a successful " + "deployment; default none"
	)]
	public string Rollback { get; init; } = "none";

	[CommandOption( "--no-verify" )]
	[Description( "Do not run each step's verify.sql after its deploy.sql; for emergencies" )]
	public bool NoVerify { get; init; }

	internal RollbackTrigger RollbackTrigger { get; private set; } = RollbackTrigger.None;

	public override ValidationResult Validate()
	{
		ValidationResult shared = base.Validate();

		if( !shared.Successful )
			return shared;

		switch( Rollback.ToLowerInvariant() )
		{
			case "none":
				RollbackTrigger = RollbackTrigger.None;

			break;

			case "on-error":
				RollbackTrigger = RollbackTrigger.OnError;

			break;

			case "on-success":
				RollbackTrigger = RollbackTrigger.OnSuccess;

			break;

			default:
				return ValidationResult.Error( "--rollback must be 'none', 'on-error' or 'on-success'" );
		}

		return ValidationResult.Success();
	}
}
