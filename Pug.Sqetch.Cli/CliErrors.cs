using Pug.Sqetch.ProjectInfoStores.FileSystem;

namespace Pug.Sqetch.Cli;

internal static class CliErrors
{
	public static string Describe( Exception exception )
		=> exception switch
		{
			UnknownUserException => "user identity not configured; run 'sqetch project user <name> <email> [--global]'",
			DuplicatePlanNameException => "a plan with this name already exists",
			DuplicateStepNameException => "a step with this name already exists in the plan",
			DuplicateReleaseNameException => "a release with this name already exists",
			UnknownPlanException unknownPlan => $"plan '{unknownPlan.Name}' does not exist",
			UnknownStepException unknownStep => $"step '{unknownStep.Name}' does not exist in plan '{unknownStep.Plan}'",
			UnknownReleaseException unknownRelease => $"release '{unknownRelease.ReleaseName}' does not exist",
			ReleaseFinalizedException => "the release is finalized and can no longer be changed",
			PlanFinalizedException => "the plan belongs to a finalized release and can no longer be changed",
			EmptyReleaseException => "a release without plans cannot be finalized",
			ReleaseDependencyRequiredException =>
				"a release must declare the release it builds on (--depends-on); only the project's first release may omit it",
			ReleaseDependantExistsException dependantExists =>
				$"release '{dependantExists.Dependency}' already has dependant release '{dependantExists.Dependant}'; releases form a single lineage — depend on the latest release instead",
			AbandonedPlanDependantsException =>
				"other plans in the release depend on this plan (use --with-dependants to remove them too)",
			IncompleteDefinitionException => "the definition is missing a name",
			ProjectStoreException store => store.Message,
			_ => exception.Message
		};
}