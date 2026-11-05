using System.ComponentModel;

using Pug.Sqetch.Planning;
using Pug.Sqetch.Planning.Models;

using Spectre.Console;
using Spectre.Console.Cli;

using PlanGraph = Pug.Sqetch.Cli.Commands.Authoring.Output.PlanGraph;
using Rows = Pug.Sqetch.Cli.Commands.Authoring.Output.Rows;

namespace Pug.Sqetch.Cli.Commands.Authoring;

/// <summary>Default command of the 'plan' branch: shows one plan's details.</summary>
public sealed class PlanInfoCommand( IAnsiConsole console ) : Command<PlanInfoCommand.Settings>
{
	public sealed class Settings : OutputSettings
	{
		[CommandOption( "-n|--name <PLAN>" )] public string? Name { get; init; }

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
			console,
			settings.Format,
			Rows.PlanJson( plan ),
			[
				( "Name", plan.Definition.Name ),
				( "Description", plan.Definition.Description ),
				( "Release", plan.Release.Length == 0 ? "(unreleased)" : plan.Release ),
				( "Depends On", Rows.Dependencies( ( plan.Definition as PlanDefinition )?.Dependencies ) ),
				( "Created", Rows.Timestamp( plan.Registration.Timestamp ) ),
				( "Created By", plan.Registration.Subject.EmailAddress )
			]
		);

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

		internal PlanSearchCriteria Criteria { get; private set; } = new();

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
				CreateUser: CreateUser
			);

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

/// <summary>
/// Prints the plan dependency graph, plans grouped by release in lineage order, as text, Mermaid
/// or DOT. A dependency outside the selection is shown with its release, so a reviewer sees what
/// the selected plans rely on.
/// </summary>
public sealed class PlanGraphCommand( IAnsiConsole console ) : Command<PlanGraphCommand.Settings>
{
	public sealed class Settings : CommandSettings
	{
		[CommandOption( "--format <FORMAT>" )]
		[Description( "text (default), mermaid or dot" )]
		public string? Format { get; init; }

		[CommandOption( "-r|--release <RELEASE>" )]
		[Description( "Only plans of this release" )]
		public string? Release { get; init; }

		[CommandOption( "--unreleased" )]
		[Description( "Only unreleased plans; with --release, those too" )]
		public bool Unreleased { get; init; }

		[CommandOption( "--from-release <RELEASE>" )]
		[Description( "Plans of this release and every later one, and the unreleased plans" )]
		public string? FromRelease { get; init; }

		internal Func<PlanGraph, IReadOnlyList<string>> Render { get; private set; } = x => x.Text();

		public override ValidationResult Validate()
		{
			switch( Format?.ToLowerInvariant() )
			{
				case null or "text":
					Render = x => x.Text();

					break;

				case "mermaid":
					Render = x => x.Mermaid();

					break;

				case "dot":
					Render = x => x.Dot();

					break;

				default:
					return ValidationResult.Error( "--format must be 'text', 'mermaid' or 'dot'" );
			}

			if( Release is not null && !NameValidation.IsValid( Release ) )
				return ValidationResult.Error( NameValidation.Error( "--release" ) );

			if( FromRelease is not null )
			{
				if( Release is not null || Unreleased )
					return ValidationResult.Error( "--from-release cannot be combined with --release or --unreleased" );

				if( !NameValidation.IsValid( FromRelease ) )
					return ValidationResult.Error( NameValidation.Error( "--from-release" ) );
			}

			return ValidationResult.Success();
		}
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		PlanGraph graph = PlanGraph.Build(
			SelectGroups( session, settings ),
			x => session.Project.GetDependencies( x )
		);

		ResultWriter.WriteText( console, settings.Render( graph ) );

		return 0;
	}

	/// <summary>
	/// The selected plans, a group per release in the engine's order. A release or the unreleased
	/// plans asked for by name are shown even when empty; otherwise only groups holding plans are.
	/// </summary>
	private static IEnumerable<(string Release, IReadOnlyList<ProjectPlan> Plans)> SelectGroups(
		ProjectSession session,
		Settings settings
	)
	{
		List<(string Release, List<ProjectPlan> Plans)> groups = [ ];

		if( settings.Release is not null || settings.Unreleased )
		{
			if( settings.Release is not null )
				groups.Add(
					(
						RequireRelease( session, settings.Release ).Definition.Name,
						session.Project.GetPlans( new PlanSearchCriteria( Release: settings.Release ) ).ToList()
					)
				);

			if( settings.Unreleased )
				groups.Add( ( "", session.Project.GetPlans( new PlanSearchCriteria() ).ToList() ) );
		}
		else
		{
			HashSet<string>? releases =
				settings.FromRelease is null ? null : LineageFrom( session, settings.FromRelease );

			// released plans arrive grouped by release in lineage order: split them into those runs
			foreach( ProjectPlan plan in session.Project.GetPlans( new PlanSearchCriteria( Released: true ) ) )
			{
				if( releases is not null && !releases.Contains( plan.Release ) )
					continue;

				if( groups.Count == 0 ||
					!string.Equals( groups[^1].Release, plan.Release, StringComparison.OrdinalIgnoreCase ) )
					groups.Add( ( plan.Release, [ ] ) );

				groups[^1].Plans.Add( plan );
			}

			List<ProjectPlan> unreleased = session.Project.GetPlans( new PlanSearchCriteria() ).ToList();

			if( unreleased.Count > 0 )
				groups.Add( ( "", unreleased ) );
		}

		return groups.Select( x => ( x.Release, (IReadOnlyList<ProjectPlan>)x.Plans ) );
	}

	private static ProjectRelease RequireRelease( ProjectSession session, string release )
		=> session.Stores.InfoStore.GetRelease( release ) ?? throw new UnknownReleaseException( release );

	/// <summary><paramref name="release"/> and every release after it, following each release's one dependant.</summary>
	private static HashSet<string> LineageFrom( ProjectSession session, string release )
	{
		HashSet<string> lineage = new( StringComparer.OrdinalIgnoreCase );

		string? current = RequireRelease( session, release ).Definition.Name;

		while( current is not null && lineage.Add( current ) )
			current = session.Project.GetReleaseDependants( current ).FirstOrDefault()?.Definition.Name;

		return lineage;
	}
}

public sealed class PlanCreateCommand( IAnsiConsole console ) : Command<PlanCreateCommand.Settings>
{
	public sealed class Settings : CommandSettings
	{
		[CommandOption( "-n|--name <PLAN>" )] public string? Name { get; init; }

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
			new PlanDefinition(
				settings.Name!,
				settings.Description ?? "",
				OptionParsing.SplitList( settings.RequirePlans )
			),
			settings.Release
		);

		if( settings.Release is not null )
			session.Project.AddPlanToRelease( settings.Release, settings.Name!, includeDependencies: false );

		console.WriteLine(
			session.Paths.RelativeToRoot( session.Paths.PlanDirectory( settings.Release ?? "", settings.Name! ) )
		);

		return 0;
	}
}

public class PlanNameSettings : CommandSettings
{
	[CommandOption( "-n|--name <PLAN>" )] public string? Name { get; init; }

	public override ValidationResult Validate()
		=> string.IsNullOrWhiteSpace( Name ) ? ValidationResult.Error( "--name is required" ) : ValidationResult.Success();
}

/// <summary>The plan and a list of plans, for the commands changing a plan's dependencies.</summary>
public class PlanDependencySettings : PlanNameSettings
{
	[CommandOption( "--plans <PLANS>" )]
	[Description( "Comma- or semicolon-separated plan names" )]
	public string? Plans { get; init; }

	public override ValidationResult Validate()
		=> Plans is null ? ValidationResult.Error( "--plans is required" ) : base.Validate();
}

/// <summary>Changes a plan's description.</summary>
public sealed class PlanUpdateCommand : Command<PlanUpdateCommand.Settings>
{
	public sealed class Settings : PlanNameSettings
	{
		[CommandOption( "-d|--description <TEXT>" )]
		public string? Description { get; init; }

		public override ValidationResult Validate()
			=> Description is null ? ValidationResult.Error( "--description is required" ) : base.Validate();
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		session.Project.UpdatePlan( settings.Name!, settings.Description!, null );

		return 0;
	}
}

/// <summary>Replaces a plan's dependencies; <c>--plans ""</c> leaves it with none.</summary>
public sealed class PlanSetDependenciesCommand : Command<PlanDependencySettings>
{
	protected override int Execute(
		CommandContext context,
		PlanDependencySettings settings,
		CancellationToken cancellationToken
	)
	{
		using ProjectSession session = ProjectSession.Open();

		session.Project.UpdatePlan( settings.Name!, null, OptionParsing.SplitList( settings.Plans ) );

		return 0;
	}
}

/// <summary>Adds plans to a plan's dependencies; one it already has is left as it is.</summary>
public sealed class PlanAddDependencyCommand : Command<PlanDependencySettings>
{
	protected override int Execute(
		CommandContext context,
		PlanDependencySettings settings,
		CancellationToken cancellationToken
	)
	{
		using ProjectSession session = ProjectSession.Open();

		List<string> dependencies = CurrentDependencies( session, settings.Name! );

		foreach( string added in OptionParsing.SplitList( settings.Plans ) )
			if( !dependencies.Contains( added, StringComparer.OrdinalIgnoreCase ) )
				dependencies.Add( added );

		session.Project.UpdatePlan( settings.Name!, null, dependencies );

		return 0;
	}

	internal static List<string> CurrentDependencies( ProjectSession session, string name )
	{
		ProjectPlan plan = session.Stores.InfoStore.GetPlan( name ) ?? throw new UnknownPlanException( name );

		return ( ( plan.Definition as PlanDefinition )?.Dependencies ?? [ ] ).ToList();
	}
}

/// <summary>
/// Removes plans from a plan's dependencies; naming one it does not depend on is refused, so a typo
/// is not taken for success.
/// </summary>
public sealed class PlanRemoveDependencyCommand : Command<PlanDependencySettings>
{
	protected override int Execute(
		CommandContext context,
		PlanDependencySettings settings,
		CancellationToken cancellationToken
	)
	{
		using ProjectSession session = ProjectSession.Open();

		List<string> dependencies = PlanAddDependencyCommand.CurrentDependencies( session, settings.Name! );

		foreach( string removed in OptionParsing.SplitList( settings.Plans ) )
			if( dependencies.RemoveAll( x => string.Equals( x, removed, StringComparison.OrdinalIgnoreCase ) ) == 0 )
				throw new InvalidOperationException( $"plan '{settings.Name}' does not depend on '{removed}'" );

		session.Project.UpdatePlan( settings.Name!, null, dependencies );

		return 0;
	}
}

public sealed class PlanDeleteCommand : Command<PlanDeleteCommand.Settings>
{
	public sealed class Settings : CommandSettings
	{
		[CommandOption( "-n|--name <PLAN>" )] public string? Name { get; init; }

		public override ValidationResult Validate()
			=> string.IsNullOrWhiteSpace( Name ) ?
				ValidationResult.Error( "--name is required" ) :
				ValidationResult.Success();
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
		[CommandOption( "-p|--plan <PLAN>" )] public string? Plan { get; init; }

		[CommandOption( "-r|--release <RELEASE>" )]
		[Description( "Release to add or move the plan to" )]
		public string? Release { get; init; }

		[CommandOption( "--unreleased" )]
		[Description( "Remove the plan from its release" )]
		public bool Unreleased { get; init; }

		[CommandOption( "--with-dependencies" )]
		[Description( "Also add the plan's unreleased dependencies to the release" )]
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

			if( WithDependants && !Unreleased )
				return ValidationResult.Error( "--with-dependants applies only with --unreleased" );

			if( WithDependencies && Unreleased )
				return ValidationResult.Error( "--with-dependencies applies only with --release" );

			return ValidationResult.Success();
		}
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		ProjectPlan plan = session.Stores.InfoStore.GetPlan( settings.Plan! ) ??
							throw new UnknownPlanException( settings.Plan! );

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

		// one engine call checks both releases before changing either, so a refused move leaves
		// the plan where it was
		session.Project.MovePlanToRelease( plan.Definition.Name, settings.Release!, settings.WithDependencies );

		return 0;
	}
}