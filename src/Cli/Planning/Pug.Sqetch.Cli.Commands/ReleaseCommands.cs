using System.ComponentModel;

using Pug.Sqetch.Planning.Models;

using Spectre.Console;
using Spectre.Console.Cli;

using Rows = Pug.Sqetch.Cli.Commands.Authoring.Output.Rows;

namespace Pug.Sqetch.Cli.Commands.Authoring;

public class ReleaseNameSettings : CommandSettings
{
	[CommandOption( "-n|--name <RELEASE>" )]
	public string? Name { get; init; }

	public override ValidationResult Validate()
		=> string.IsNullOrWhiteSpace( Name ) ?
			ValidationResult.Error( "--name is required" ) :
			ValidationResult.Success();
}

public sealed class ReleaseCreateCommand( IAnsiConsole console ) : Command<ReleaseCreateCommand.Settings>
{
	public sealed class Settings : ReleaseNameSettings
	{
		[CommandOption( "-d|--description <TEXT>" )]
		public string? Description { get; init; }

		[CommandOption( "--depends-on <RELEASE>" )]
		[Description( "The release this release builds on" )]
		public string? DependsOn { get; init; }

		[CommandOption( "--plans <PLANS>" )]
		[Description( "Comma- or semicolon-separated names of plans to include" )]
		public string? Plans { get; init; }

		[CommandOption( "--all-unreleased" )]
		[Description( "Include every currently unreleased plan" )]
		public bool AllUnreleased { get; init; }

		public override ValidationResult Validate()
		{
			if( Plans is not null && AllUnreleased )
				return ValidationResult.Error( "specify either --plans or --all-unreleased, not both" );

			if( !NameValidation.IsValid( Name ) )
				return ValidationResult.Error( NameValidation.Error( "--name" ) );

			return base.Validate();
		}
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		ReleaseDefinition definition = new( settings.Name!, settings.Description ?? "", settings.DependsOn ?? "" );

		if( settings.Plans is not null )
			session.Project.CreateRelease( definition, OptionParsing.SplitList( settings.Plans ) );
		else
			session.Project.CreateRelease( definition, settings.AllUnreleased );

		console.WriteLine( $"created release '{settings.Name}'" );

		return 0;
	}
}

public sealed class ReleaseListCommand( IAnsiConsole console ) : Command<ReleaseListCommand.Settings>
{
	public sealed class Settings : OutputSettings
	{
		[CommandOption( "--prefix <PREFIX>" )]
		[Description( "Only releases whose name starts with this prefix" )]
		public string? Prefix { get; init; }

		[CommandOption( "--created-after <DATE>" )]
		public string? CreatedAfter { get; init; }

		[CommandOption( "--created-before <DATE>" )]
		public string? CreatedBefore { get; init; }

		[CommandOption( "--finalized" )]
		[Description( "List finalized releases instead of open ones" )]
		public bool Finalized { get; init; }

		[CommandOption( "--finalized-after <DATE>" )]
		public string? FinalizedAfter { get; init; }

		[CommandOption( "--finalized-before <DATE>" )]
		public string? FinalizedBefore { get; init; }

		[CommandOption( "--create-user <EMAIL>" )]
		[Description( "Only releases created by this user" )]
		public string? CreateUser { get; init; }

		[CommandOption( "--finalize-user <EMAIL>" )]
		[Description( "Only releases finalized by this user (implies --finalized)" )]
		public string? FinalizeUser { get; init; }

		internal ReleaseSearchCriteria Criteria { get; private set; } = new();

		public override ValidationResult Validate()
		{
			if( !OptionParsing.TryParseDate( CreatedAfter, out DateTime? createdAfter ) )
				return ValidationResult.Error( "--created-after must be a yyyy-MM-dd date" );

			if( !OptionParsing.TryParseDate( CreatedBefore, out DateTime? createdBefore ) )
				return ValidationResult.Error( "--created-before must be a yyyy-MM-dd date" );

			if( !OptionParsing.TryParseDate( FinalizedAfter, out DateTime? finalizedAfter ) )
				return ValidationResult.Error( "--finalized-after must be a yyyy-MM-dd date" );

			if( !OptionParsing.TryParseDate( FinalizedBefore, out DateTime? finalizedBefore ) )
				return ValidationResult.Error( "--finalized-before must be a yyyy-MM-dd date" );

			Criteria = new ReleaseSearchCriteria(
				Prefix: Prefix ?? "",
				Finalized: Finalized,
				CreateTimestamp: OptionParsing.Window( createdAfter, createdBefore ),
				FinalizeTimestamp: OptionParsing.Window( finalizedAfter, finalizedBefore ),
				CreateUser: CreateUser,
				FinalizeUser: FinalizeUser
			);

			return base.Validate();
		}
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		List<ProjectRelease> releases = session.Project.GetReleases( settings.Criteria ).ToList();

		ResultWriter.WriteRows(
			console,
			settings.Format,
			Rows.ReleaseHeaders,
			releases,
			Rows.Release,
			Rows.ReleaseJson
		);

		return 0;
	}
}

public sealed class ReleaseDeleteCommand : Command<ReleaseNameSettings>
{
	protected override int Execute(
		CommandContext context,
		ReleaseNameSettings settings,
		CancellationToken cancellationToken
	)
	{
		using ProjectSession session = ProjectSession.Open();

		session.Project.DeleteRelease( settings.Name! );

		return 0;
	}
}

public sealed class ReleaseFinalizeCommand( IAnsiConsole console ) : Command<ReleaseNameSettings>
{
	protected override int Execute(
		CommandContext context,
		ReleaseNameSettings settings,
		CancellationToken cancellationToken
	)
	{
		using ProjectSession session = ProjectSession.Open();

		session.Project.FinalizeRelease( settings.Name! );

		console.WriteLine( $"finalized release '{settings.Name}'" );

		return 0;
	}
}