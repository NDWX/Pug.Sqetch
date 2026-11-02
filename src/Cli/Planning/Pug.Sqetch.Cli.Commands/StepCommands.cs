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

		[CommandOption( "--requires <STEPS>" )]
		[Description( "Comma- or semicolon-separated names of steps this step depends on" )]
		public string? Requires { get; init; }

		public override ValidationResult Validate()
		{
			if( string.IsNullOrWhiteSpace( Name ) )
				return ValidationResult.Error( "--name is required" );

			if( !NameValidation.IsValid( Name ) )
				return ValidationResult.Error( NameValidation.Error( "--name" ) );

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