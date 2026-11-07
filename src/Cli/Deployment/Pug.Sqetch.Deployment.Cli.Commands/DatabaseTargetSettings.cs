using System.ComponentModel;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Deployment.Cli.Commands;

/// <summary>Switches shared by the deploy and rollback commands: which bundle, which database, and how to run.</summary>
public class DatabaseTargetSettings : CommandSettings
{
	[CommandArgument( 0, "<BUNDLE>" )]
	[Description( "Bundle archive file or directory" )]
	public string Bundle { get; init; } = null!;

	[CommandOption( "-d|--driver <NAME>" )]
	[Description(
		"Database driver; its parameters are passed as --<driver>-<parameter> <value>. " +
		"See 'drivers list' and 'drivers parameters <driver>'"
	)]
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

	[CommandOption( "--log <PATH>" )]
	[Description( "Append timestamped progress lines to this log file" )]
	public string? Log { get; init; }

	internal DeploymentCommitLevel EffectiveCommitLevel { get; private set; } = DeploymentCommitLevel.Release;

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

		return ValidationResult.Success();
	}
}