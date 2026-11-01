using System.ComponentModel;
using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Cli.Commands.Authoring;

public sealed class ProjectInitCommand( IAnsiConsole console ) : Command<ProjectInitCommand.Settings>
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument( 0, "<name>" )]
		[Description( "Name of the new Sqetch project" )]
		public string Name { get; init; } = "";

		[CommandOption( "-d|--description <TEXT>" )]
		public string? Description { get; init; }

		[CommandOption( "--engine <ENGINE>" )]
		[Description( "Database engine the project targets" )]
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
			string delimiter = string.IsNullOrEmpty( settings.ShardByPrefix.Value ) ? "." : settings.ShardByPrefix.Value;

			sharding = new ShardingConfiguration(
				VersionPrefixShardingStrategy.StrategyName,
				new Dictionary<string, string> { [VersionPrefixShardingStrategy.DelimiterOption] = delimiter } );
		}

		FileSystemProjectStores.Initialize(
				new ProjectDefinition( settings.Name, settings.Description ?? "", settings.Engine ?? "" ),
				new ActionContext( UserIdentity.Require( projectPath ), DateTime.Now ),
				projectPath, sharding )
			.Dispose();

		console.WriteLine( $"initialized Sqetch project in {projectPath}" );

		return 0;
	}
}

public sealed class ProjectUserCommand( IAnsiConsole console ) : Command<ProjectUserCommand.Settings>
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument( 0, "<name>" )]
		public string Name { get; init; } = "";

		[CommandArgument( 1, "<email>" )]
		public string Email { get; init; } = "";

		[CommandOption( "-g|--global" )]
		[Description( "Save the identity in the home directory instead of the project" )]
		public bool Global { get; init; }
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		string directory = UserIdentity.Save(
			new UserInfo( settings.Name, settings.Email ), Directory.GetCurrentDirectory(), settings.Global );

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
