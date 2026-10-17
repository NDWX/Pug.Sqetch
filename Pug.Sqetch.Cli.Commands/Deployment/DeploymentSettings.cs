using System.ComponentModel;
using Pug.Sqetch.Deployment;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Cli.Commands.Deployment;

/// <summary>Switches shared by the deploy and rollback commands.</summary>
public class DeploymentSettings : CommandSettings
{
	[CommandArgument( 0, "<BUNDLE>" )]
	[Description( "Bundle archive file or directory" )]
	public string Bundle { get; init; } = null!;

	[CommandOption( "-d|--driver <NAME>" )]
	[Description(
		"Database driver; its parameters are passed as --<driver>-<parameter> <value>. "
		+ "See 'drivers list' and 'drivers parameters <driver>'" )]
	public string? Driver { get; init; }

	[CommandOption( "-t|--bundle-type <TYPE>" )]
	[Description( "Bundle type: zip, tar, tar.gz or directory; inferred from the bundle path when omitted" )]
	public string? BundleType { get; init; }

	[CommandOption( "--timeout <SECONDS>" )]
	[Description( "Step script timeout in seconds, 0 for no timeout; default 300" )]
	public int TimeoutSeconds { get; init; } = 300;

	[CommandOption( "--commit-level <LEVEL>" )]
	[Description( "Commit progress after every 'plan' or only after every 'release'; default release" )]
	public string CommitLevel { get; init; } = "release";

	[CommandOption( "--rollback <MODE>" )]
	[Description(
		"Undo the deployment: 'on-error' after a failure, 'on-success' after a successful "
		+ "deployment; default none" )]
	public string Rollback { get; init; } = "none";

	[CommandOption( "--log <PATH>" )]
	[Description( "Append timestamped progress lines to this log file" )]
	public string? Log { get; init; }

	internal DeploymentCommitLevel EffectiveCommitLevel { get; private set; } = DeploymentCommitLevel.Release;

	internal RollbackTrigger RollbackTrigger { get; private set; } = RollbackTrigger.None;

	internal TimeSpan StepScriptTimeout
		=> TimeoutSeconds == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds( TimeoutSeconds );

	public override ValidationResult Validate()
	{
		if( string.IsNullOrEmpty( Driver ) )
			return ValidationResult.Error( "--driver is required; 'drivers list' names the ones this host offers" );

		if( TimeoutSeconds < 0 )
			return ValidationResult.Error( "--timeout must be zero or a positive number of seconds" );

		if( string.Equals( CommitLevel, "plan", StringComparison.OrdinalIgnoreCase ) )
			EffectiveCommitLevel = DeploymentCommitLevel.Plan;
		else if( !string.Equals( CommitLevel, "release", StringComparison.OrdinalIgnoreCase ) )
			return ValidationResult.Error( "--commit-level must be 'plan' or 'release'" );

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
