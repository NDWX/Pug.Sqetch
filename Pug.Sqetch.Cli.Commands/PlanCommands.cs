using System.ComponentModel;
using Pug.Sqetch.Planning;
using Pug.Sqetch.Planning.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using Rows = Pug.Sqetch.Cli.Commands.Authoring.Output.Rows;

namespace Pug.Sqetch.Cli.Commands.Authoring;

/// <summary>Default command of the 'plan' branch: shows one plan's details.</summary>
public sealed class PlanInfoCommand( IAnsiConsole console ) : Command<PlanInfoCommand.Settings>
{
	public sealed class Settings : OutputSettings
	{
		[CommandOption( "-n|--name <PLAN>" )]
		public string? Name { get; init; }

		public override ValidationResult Validate()
		{
			if( string.IsNullOrWhiteSpace( Name ) )
				return ValidationResult.Error( "--name is required" );

			return base.Validate();
		}
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		ProjectPlan? plan = session.Stores.InfoStore.GetPlan( settings.Name! );

		if( plan is null )
			throw new UnknownPlanException( settings.Name! );

		ResultWriter.WriteDetails(
			console, settings.Format, Rows.PlanJson( plan ),
			[
				("Name", plan.Definition.Name),
				("Description", plan.Definition.Description),
				("Release", plan.Release.Length == 0 ? "(unreleased)" : plan.Release),
				("Depends On", Rows.Dependencies( ( plan.Definition as PlanDefinition )?.Dependencies )),
				("Created", Rows.Timestamp( plan.Registration.Timestamp )),
				("Created By", plan.Registration.Subject.EmailAddress)
			] );

		return 0;
	}
}

public sealed class PlanListCommand( IAnsiConsole console ) : Command<PlanListCommand.Settings>
{
	public sealed class Settings : OutputSettings
	{
		[CommandOption( "-r|--release <RELEASE>" )]
		[Description( "Only plans of this release" )]
		public string? Release { get; init; }

		[CommandOption( "--released" )]
		[Description( "Only plans that belong to a release" )]
		public bool Released { get; init; }

		[CommandOption( "--finalized-after <DATE>" )]
		[Description( "Only plans of releases finalized on or after this date (yyyy-MM-dd)" )]
		public string? FinalizedAfter { get; init; }

		[CommandOption( "--finalized-before <DATE>" )]
		[Description( "Only plans of releases finalized before this date (yyyy-MM-dd)" )]
		public string? FinalizedBefore { get; init; }

		[CommandOption( "-u|--create-user <EMAIL>" )]
		[Description( "Only plans created by this user" )]
		public string? CreateUser { get; init; }

		internal PlanSearchCriteria Criteria { get; private set; } = new ();

		public override ValidationResult Validate()
		{
			if( !OptionParsing.TryParseDate( FinalizedAfter, out DateTime? after ) )
				return ValidationResult.Error( "--finalized-after must be a yyyy-MM-dd date" );

			if( !OptionParsing.TryParseDate( FinalizedBefore, out DateTime? before ) )
				return ValidationResult.Error( "--finalized-before must be a yyyy-MM-dd date" );

			Range<DateTime>? window = OptionParsing.Window( after, before );

			Criteria = new PlanSearchCriteria(
				Release: Release,
				Released: Released || Release is not null || window is not null,
				ReleaseFinalizeTimestamp: window,
				CreateUser: CreateUser );

			return base.Validate();
		}
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		List<ProjectPlan> plans = session.Project.GetPlans( settings.Criteria ).ToList();

		ResultWriter.WriteRows( console, settings.Format, Rows.PlanHeaders, plans, Rows.Plan, Rows.PlanJson );

		return 0;
	}
}

public sealed class PlanCreateCommand( IAnsiConsole console ) : Command<PlanCreateCommand.Settings>
{
	public sealed class Settings : CommandSettings
	{
		[CommandOption( "-n|--name <PLAN>" )]
		public string? Name { get; init; }

		[CommandOption( "-d|--description <TEXT>" )]
		public string? Description { get; init; }

		[CommandOption( "--require-plans <PLANS>" )]
		[Description( "Comma- or semicolon-separated names of plans this plan depends on" )]
		public string? RequirePlans { get; init; }

		[CommandOption( "-r|--release <RELEASE>" )]
		[Description( "Add the new plan to this release" )]
		public string? Release { get; init; }

		public override ValidationResult Validate()
		{
			if( string.IsNullOrWhiteSpace( Name ) )
				return ValidationResult.Error( "--name is required" );

			if( !NameValidation.IsValid( Name ) )
				return ValidationResult.Error( NameValidation.Error( "--name" ) );

			return ValidationResult.Success();
		}
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		session.Project.Add(
			new PlanDefinition( settings.Name!, settings.Description ?? "", OptionParsing.SplitList( settings.RequirePlans ) ),
			settings.Release );

		if( settings.Release is not null )
			session.Project.AddPlanToRelease( settings.Release, settings.Name!, includeDependencies: false );

		console.WriteLine(
			session.Paths.RelativeToRoot( session.Paths.PlanDirectory( settings.Release ?? "", settings.Name! ) ) );

		return 0;
	}
}

public sealed class PlanDeleteCommand : Command<PlanDeleteCommand.Settings>
{
	public sealed class Settings : CommandSettings
	{
		[CommandOption( "-n|--name <PLAN>" )]
		public string? Name { get; init; }

		public override ValidationResult Validate()
			=> string.IsNullOrWhiteSpace( Name )
				? ValidationResult.Error( "--name is required" )
				: ValidationResult.Success();
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		session.Project.DeletePlan( settings.Name! );

		return 0;
	}
}

public sealed class PlanSetReleaseCommand( IAnsiConsole console ) : Command<PlanSetReleaseCommand.Settings>
{
	public sealed class Settings : CommandSettings
	{
		[CommandOption( "-p|--plan <PLAN>" )]
		public string? Plan { get; init; }

		[CommandOption( "-r|--release <RELEASE>" )]
		[Description( "Release to add or move the plan to" )]
		public string? Release { get; init; }

		[CommandOption( "--unreleased" )]
		[Description( "Remove the plan from its release" )]
		public bool Unreleased { get; init; }

		[CommandOption( "--with-dependencies" )]
		[Description( "Also pull the plan's dependencies into the release" )]
		public bool WithDependencies { get; init; }

		[CommandOption( "--with-dependants" )]
		[Description( "When removing, also remove plans that depend on this one" )]
		public bool WithDependants { get; init; }

		public override ValidationResult Validate()
		{
			if( string.IsNullOrWhiteSpace( Plan ) )
				return ValidationResult.Error( "--plan is required" );

			if( Unreleased == ( Release is not null ) )
				return ValidationResult.Error( "specify either --release <release> or --unreleased" );

			return ValidationResult.Success();
		}
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		ProjectPlan plan = session.Stores.InfoStore.GetPlan( settings.Plan! )
							?? throw new UnknownPlanException( settings.Plan! );

		if( settings.Unreleased )
		{
			if( plan.Release.Length == 0 )
			{
				console.WriteLine( $"plan '{plan.Definition.Name}' is not part of a release" );
				return 0;
			}

			session.Project.RemovePlanFromRelease( plan.Release, plan.Definition.Name, settings.WithDependants );
			return 0;
		}

		if( string.Equals( plan.Release, settings.Release, StringComparison.OrdinalIgnoreCase ) )
		{
			console.WriteLine( $"plan '{plan.Definition.Name}' is already part of release '{plan.Release}'" );
			return 0;
		}

		// moving between releases = remove from the current one, then add to the target,
		// so every business validation applies on both sides
		if( plan.Release.Length > 0 )
			session.Project.RemovePlanFromRelease( plan.Release, plan.Definition.Name, settings.WithDependants );

		session.Project.AddPlanToRelease( settings.Release!, plan.Definition.Name, settings.WithDependencies );

		return 0;
	}
}
