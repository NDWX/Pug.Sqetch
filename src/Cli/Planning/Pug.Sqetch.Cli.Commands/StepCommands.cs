using System.ComponentModel;

using Pug.Sqetch.Planning.Models;

using Spectre.Console;
using Spectre.Console.Cli;

using Rows = Pug.Sqetch.Cli.Commands.Authoring.Output.Rows;

namespace Pug.Sqetch.Cli.Commands.Authoring;

public class StepSettings : CommandSettings
{
	[CommandOption( "-p|--plan <PLAN>" )] public string? Plan { get; init; }

	public override ValidationResult Validate()
		=> string.IsNullOrWhiteSpace( Plan ) ?
			ValidationResult.Error( "--plan is required" ) :
			ValidationResult.Success();
}

public sealed class PlanAddStepCommand( IAnsiConsole console ) : Command<PlanAddStepCommand.Settings>
{
	public sealed class Settings : StepSettings
	{
		[CommandOption( "-n|--name <STEP>" )] public string? Name { get; init; }

		[CommandOption( "-d|--description <TEXT>" )]
		public string? Description { get; init; }

		[CommandOption( "--requires <STEP>" )]
		[Description( "The step this step depends on; at most one" )]
		public string? Requires { get; init; }

		public override ValidationResult Validate()
		{
			if( string.IsNullOrWhiteSpace( Name ) )
				return ValidationResult.Error( "--name is required" );

			if( !NameValidation.IsValid( Name ) )
				return ValidationResult.Error( NameValidation.Error( "--name" ) );

			if( OptionParsing.SplitList( Requires ).Length > 1 )
				return ValidationResult.Error( "--requires takes one step: a step requires at most one step" );

			return base.Validate();
		}
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		StepScriptKeys keys = session.Project.Add(
			new StepDefinition(
				settings.Plan!,
				settings.Name!,
				settings.Description ?? "",
				OptionParsing.SplitList( settings.Requires )
			),
			settings.Plan!
		);

		ProjectPlan plan = session.Stores.InfoStore.GetPlan( settings.Plan! )!;

		console.WriteLine(
			session.Paths.RelativeToRoot( session.Paths.StepDirectory( plan.Release, settings.Plan!, settings.Name! ) )
		);

		// seed empty script files so they are ready to edit and visible to git
		foreach( ( string key, string kind ) in new[]
				{
					( keys.DeployScript, "deploy" ), ( keys.VerifyScript, "verify" ),
					( keys.RollbackScript, "rollback" )
				} )
		{
			string path = Path.Combine( session.Root, key.Replace( '/', Path.DirectorySeparatorChar ) );

			if( !File.Exists( path ) )
				File.WriteAllText( path, $"-- {kind} script for step '{settings.Name}' of plan '{settings.Plan}'\n" );

			console.WriteLine( key );
		}

		return 0;
	}
}

public sealed class PlanListStepsCommand( IAnsiConsole console ) : Command<PlanListStepsCommand.Settings>
{
	public sealed class Settings : StepSettings
	{
		[CommandOption( "-o|--output <FORMAT>" )]
		public string? Output { get; init; }

		internal OutputFormat Format { get; private set; }

		public override ValidationResult Validate()
		{
			if( !OutputFormats.TryParse( Output, out OutputFormat format ) )
				return ValidationResult.Error( "--output must be 'json' or 'csv'" );

			Format = format;

			return base.Validate();
		}
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		List<ProjectElement> steps = session.Project.GetSteps( settings.Plan! ).ToList();

		ResultWriter.WriteRows( console, settings.Format, Rows.StepHeaders, steps, Rows.Step, Rows.StepJson );

		return 0;
	}
}

public sealed class PlanDeleteStepCommand : Command<PlanDeleteStepCommand.Settings>
{
	public sealed class Settings : StepSettings
	{
		[CommandOption( "-n|--name <STEP>" )] public string? Name { get; init; }

		public override ValidationResult Validate()
			=> string.IsNullOrWhiteSpace( Name ) ? ValidationResult.Error( "--name is required" ) : base.Validate();
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		session.Project.Delete( settings.Plan!, settings.Name! );

		return 0;
	}
}
public sealed class PlanUpdateStepCommand : Command<PlanUpdateStepCommand.Settings>
{
	public sealed class Settings : StepSettings
	{
		[CommandOption( "-n|--name <STEP>" )] public string? Name { get; init; }

		[CommandOption( "-d|--description <TEXT>" )]
		public string? Description { get; init; }

		public override ValidationResult Validate()
		{
			if( string.IsNullOrWhiteSpace( Name ) )
				return ValidationResult.Error( "--name is required" );

			if( Description is null )
				return ValidationResult.Error( "--description is required" );

			return base.Validate();
		}
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		session.Project.UpdateStep( settings.Plan!, settings.Name!, settings.Description! );

		return 0;
	}
}

/// <summary>Renames a step, its scripts included; prints the step's new path.</summary>
public sealed class PlanRenameStepCommand( IAnsiConsole console ) : Command<PlanRenameStepCommand.Settings>
{
	public sealed class Settings : StepSettings
	{
		[CommandOption( "-n|--name <STEP>" )] public string? Name { get; init; }

		[CommandOption( "--new-name <STEP>" )]
		[Description( "The step's new name" )]
		public string? NewName { get; init; }

		public override ValidationResult Validate()
		{
			if( string.IsNullOrWhiteSpace( Name ) )
				return ValidationResult.Error( "--name is required" );

			if( string.IsNullOrWhiteSpace( NewName ) )
				return ValidationResult.Error( "--new-name is required" );

			if( !NameValidation.IsValid( NewName ) )
				return ValidationResult.Error( NameValidation.Error( "--new-name" ) );

			if( string.Equals( Name, NewName, StringComparison.OrdinalIgnoreCase ) )
				return ValidationResult.Error( "--new-name must differ from the current name by more than case" );

			return base.Validate();
		}
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		session.Project.RenameStep( settings.Plan!, settings.Name!, settings.NewName! );

		ProjectPlan plan = session.Stores.InfoStore.GetPlan( settings.Plan! )!;

		console.WriteLine(
			session.Paths.RelativeToRoot(
				session.Paths.StepDirectory( plan.Release, plan.Definition.Name, settings.NewName! )
			)
		);

		return 0;
	}
}

/// <summary>
/// Moves a step right before or after another by changing dependencies, and prints each step whose
/// dependency changed, since a move can take a dependency away from a step that had it.
/// </summary>
public sealed class PlanMoveStepCommand( IAnsiConsole console ) : Command<PlanMoveStepCommand.Settings>
{
	public sealed class Settings : StepSettings
	{
		[CommandOption( "-n|--name <STEP>" )] public string? Name { get; init; }

		[CommandOption( "--before <STEP>" )]
		[Description( "Move the step right before this one" )]
		public string? Before { get; init; }

		[CommandOption( "--after <STEP>" )]
		[Description( "Move the step right after this one" )]
		public string? After { get; init; }

		public override ValidationResult Validate()
		{
			if( string.IsNullOrWhiteSpace( Name ) )
				return ValidationResult.Error( "--name is required" );

			if( ( Before is null ) == ( After is null ) )
				return ValidationResult.Error( "specify either --before <step> or --after <step>" );

			if( string.Equals( Name, Before ?? After, StringComparison.OrdinalIgnoreCase ) )
				return ValidationResult.Error( "a step cannot be moved next to itself" );

			return base.Validate();
		}
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		IReadOnlyList<StepDependencyChange> changes = session.Project.MoveStep(
			settings.Plan!,
			settings.Name!,
			settings.Before is null ? StepPlacement.After : StepPlacement.Before,
			settings.Before ?? settings.After!
		);

		if( changes.Count == 0 )
		{
			console.WriteLine( $"step '{settings.Name}' is already there" );

			return 0;
		}

		foreach( StepDependencyChange change in changes )
			console.WriteLine( $"{change.Step}: requires {Named( change.Dependency )} (was {Named( change.Previous )})" );

		return 0;
	}

	private static string Named( string dependency ) => dependency.Length == 0 ? "nothing" : dependency;
}
