using Pug.Sqetch.Cli.Commands.Authoring;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Cli;

/// <summary>
/// The 'sqetch' command tree. Kept separate from the entry point so tests can host the
/// exact same configuration in a <c>CommandAppTester</c>.
/// </summary>
public static class SqetchApp
{
	public static void Configure( IConfigurator config )
	{
		config.SetApplicationName( "sqetch" );

		// '--version' prints the version the release was built as (the tag's, plus any pre-release suffix)
		config.UseAssemblyInformationalVersion();

		config.SetExceptionHandler( ( exception, resolver ) =>
		{
			IAnsiConsole console = resolver?.Resolve( typeof(IAnsiConsole) ) as IAnsiConsole ?? AnsiConsole.Console;

			if( exception is CommandAppException { Pretty: not null } parseError )
				console.Write( parseError.Pretty );
			else
				console.MarkupLineInterpolated( $"[red]error:[/] {CliErrors.Describe( exception )}" );

			return 1;
		} );

		config.AddBranch( "project", project =>
		{
			project.SetDescription( "Initialize a project, configure the acting user, maintain the plan index" );

			project.AddCommand<ProjectInitCommand>( "init" )
					.WithDescription( "Initialize a Sqetch project in the current directory" );

			project.AddCommand<ProjectUserCommand>( "user" )
					.WithDescription( "Save the acting user's identity" );

			project.AddCommand<ProjectReindexCommand>( "reindex" )
					.WithDescription( "Rebuild the derived plan index from the plan folders" );
		} );

		config.AddBranch( "plan", plan =>
		{
			plan.SetDescription( "Create, inspect, list and release plans and their steps" );

			plan.SetDefaultCommand<PlanInfoCommand>();

			plan.AddCommand<PlanListCommand>( "list" )
				.WithDescription( "List plans; unreleased ones by default" );

			plan.AddCommand<PlanCreateCommand>( "create" )
				.WithDescription( "Create a plan; prints the plan's path" );

			plan.AddCommand<PlanDeleteCommand>( "delete" )
				.WithDescription( "Delete a plan" );

			plan.AddCommand<PlanAddStepCommand>( "add-step" )
				.WithDescription( "Add a step to a plan; prints the step and script paths" );

			plan.AddCommand<PlanListStepsCommand>( "list-steps" )
				.WithDescription( "List a plan's steps in dependency order" );

			plan.AddCommand<PlanDeleteStepCommand>( "delete-step" )
				.WithDescription( "Delete a step from a plan" );

			plan.AddCommand<PlanSetReleaseCommand>( "set-release" )
				.WithDescription( "Add or move a plan to a release, or make it unreleased" );
		} );

		config.AddBranch( "release", release =>
		{
			release.SetDescription( "Create, list, finalize and delete releases" );

			release.AddCommand<ReleaseCreateCommand>( "create" )
					.WithDescription( "Create a release" );

			release.AddCommand<ReleaseListCommand>( "list" )
					.WithDescription( "List releases; open ones by default" );

			release.AddCommand<ReleaseDeleteCommand>( "delete" )
					.WithDescription( "Delete an open release, unassigning its plans" );

			release.AddCommand<ReleaseFinalizeCommand>( "finalize" )
					.WithDescription( "Finalize a release, freezing it and its plans" );
		} );

		config.AddBranch( "journaling", journaling =>
		{
			journaling.SetDescription( "Author and inspect the project's journaling SQL" );

			journaling.AddCommand<JournalingListCommand>( "list" )
					.WithDescription( "List every journaling slot and whether it is set" );

			journaling.AddCommand<JournalingParametersCommand>( "parameters" )
					.WithDescription( "List the parameters each slot's statements may use" );

			journaling.AddCommand<JournalingPrintCommand>( "print" )
					.WithDescription( "Print one slot's SQL verbatim" );

			journaling.AddCommand<JournalingSetCommand>( "set" )
					.WithDescription( "Set one slot's SQL from an argument, --file or --stdin" );
		} );
	}
}
