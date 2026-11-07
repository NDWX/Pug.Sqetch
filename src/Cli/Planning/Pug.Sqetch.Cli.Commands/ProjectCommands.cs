using System.ComponentModel;

using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Cli.Commands.Authoring;

public sealed class ProjectInitCommand( IAnsiConsole console, IDatabaseDriverRegistry drivers, WarningWriter warnings )
	: Command<ProjectInitCommand.Settings>
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument( 0, "<name>" )]
		[Description( "Name of the new Sqetch project" )]
		public string Name { get; init; } = "";

		[CommandOption( "-d|--description <TEXT>" )]
		public string? Description { get; init; }

		[CommandOption( "--engine <ENGINE>" )]
		[Description( "Database engine the project targets; naming a database driver (see 'sqetch journaling templates') lets 'journaling init' default to it" )]
		public string? Engine { get; init; }

		[CommandOption( "--shard-by-prefix [DELIMITER]" )]
		[Description( "Shard release folders by name prefix (default delimiter '.')" )]
		public FlagValue<string>? ShardByPrefix { get; init; }
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		string projectPath = Directory.GetCurrentDirectory();

		ShardingConfiguration? sharding = null;

		if( settings.ShardByPrefix?.IsSet == true )
		{
			string delimiter = string.IsNullOrEmpty( settings.ShardByPrefix.Value ) ?
				"." :
				settings.ShardByPrefix.Value;

			sharding = new ShardingConfiguration(
				VersionPrefixShardingStrategy.StrategyName,
				new Dictionary<string, string> { [VersionPrefixShardingStrategy.DelimiterOption] = delimiter }
			);
		}

		FileSystemProjectStores.Initialize(
				new ProjectDefinition( settings.Name, settings.Description ?? "", settings.Engine ?? "" ),
				new ActionContext( UserIdentity.Require( projectPath ), DateTime.Now ),
				projectPath,
				sharding
			)
			.Dispose();

		console.WriteLine( $"initialized Sqetch project in {projectPath}" );

		// free text, so not refused; but only a driver's name lets 'journaling init' default to it
		if( !string.IsNullOrWhiteSpace( settings.Engine ) && !drivers.TryCreate( settings.Engine, out _ ) )
			warnings.Write(
				$"engine '{settings.Engine}' names no database driver ({string.Join( ", ", drivers.Names.Order( StringComparer.OrdinalIgnoreCase ) )}); " +
				"'sqetch journaling init' will need the driver passed explicitly" );

		return 0;
	}
}

public sealed class ProjectUserCommand( IAnsiConsole console ) : Command<ProjectUserCommand.Settings>
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument( 0, "<name>" )] public string Name { get; init; } = "";

		[CommandArgument( 1, "<email>" )] public string Email { get; init; } = "";

		[CommandOption( "-g|--global" )]
		[Description( "Save the identity in the home directory instead of the project" )]
		public bool Global { get; init; }
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		string directory = UserIdentity.Save(
			new UserInfo( settings.Name, settings.Email ),
			Directory.GetCurrentDirectory(),
			settings.Global
		);

		console.WriteLine( $"user identity saved to {directory}" );

		return 0;
	}
}

public sealed class ProjectReindexCommand( IAnsiConsole console ) : Command
{
	protected override int Execute( CommandContext context, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		session.Stores.Reindex();

		console.WriteLine( "plan index rebuilt" );

		return 0;
	}
}

/// <summary>
/// Reports what is inconsistent in the project, changing nothing unless asked to rebuild the index:
/// the store's findings (the project on disk) first, then — only when none of them makes the plan
/// index untrustworthy — the engine's (the rules). Exits <see cref="FindingsExitCode"/> when it finds
/// anything, so CI can fail on it, keeping 1 for the check itself failing.
/// </summary>
public sealed class ProjectCheckCommand( IAnsiConsole console ) : Command<ProjectCheckCommand.Settings>
{
	/// <summary>What 'project check' returns when it found something.</summary>
	public const int FindingsExitCode = 2;

	public sealed class Settings : OutputSettings
	{
		[CommandOption( "--fix" )]
		[Description( "Rebuild the plan index first, as 'project reindex' does; everything else needs a person" )]
		public bool Fix { get; init; }
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		bool interactive = settings.Format == OutputFormat.Auto && console.Profile.Capabilities.Interactive;

		List<ProjectFinding> findings = session.Stores.Check().ToList();

		// reindex only when it can resolve everything that blocks: over an unreadable document it would
		// fail partway, and over a duplicate or a name mismatch it would pick a copy no person chose
		bool fixRefused = settings.Fix &&
						findings.Any( finding => ProjectChecks.IsBlocking( finding.Check ) && !finding.FixedByReindex );

		if( settings.Fix && !fixRefused )
		{
			session.Stores.Reindex();

			// only where a person reads it: machine output is the findings alone
			if( interactive )
				console.WriteLine( "plan index rebuilt" );

			findings = session.Stores.Check().ToList();
		}

		// the rules read through the plan index and the documents: from untrustworthy ones they would
		// fail or report what is not there
		List<ProjectFinding> blocking = findings.Where( finding => ProjectChecks.IsBlocking( finding.Check ) ).ToList();

		if( blocking.Count > 0 )
			findings.Add(
				new ProjectFinding(
					ProjectChecks.ChecksSkipped,
					"",
					"",
					"",
					"the remaining checks were not run, since what they read cannot be trusted yet; " +
					( fixRefused ? "--fix was not applied, as reindexing cannot resolve them all; " : "" ) +
					( blocking.All( finding => finding.FixedByReindex ) ?
						"run 'sqetch project check --fix' (or 'sqetch project reindex') to check the rest" :
						"resolve the findings --fix cannot repair, then check again" )
				)
			);
		else
			findings.AddRange( session.ReadOnlyProject.Check() );

		if( findings.Count == 0 && interactive )
			console.WriteLine( "no problems found" );
		else
			ResultWriter.WriteRows(
				console,
				settings.Format,
				[ "Check", "Release", "Plan", "Step", "Problem" ],
				findings,
				finding => [ finding.Check, finding.Release, finding.Plan, finding.Step, finding.Message ],
				finding => new
				{
					check = finding.Check,
					release = finding.Release,
					plan = finding.Plan,
					step = finding.Step,
					problem = finding.Message,
					fixedByReindex = finding.FixedByReindex
				}
			);

		return findings.Count > 0 ? FindingsExitCode : 0;
	}
}
