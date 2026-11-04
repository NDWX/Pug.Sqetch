using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

namespace Pug.Sqetch.Planning.Cli;

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
			UnknownStepException unknownStep =>
				$"step '{unknownStep.Name}' does not exist in plan '{unknownStep.Plan}'",
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
			MissingPlanDependenciesException missing => DescribeMissingDependencies( missing ),
			StrandedPlanDependantsException stranded =>
				$"plans in releases before '{stranded.Release}' depend on '{stranded.Plan}': " +
				string.Join( ", ", stranded.Dependants.Select( x => $"'{x.Plan}' ({x.Release})" ) ) +
				$"; move them to '{stranded.Release}' or a later release first",
			IncompleteDefinitionException => "the definition is missing a name",
			ProjectStoreException store => store.Message,
			_ => exception.Message
		};

	// names every missing plan with the plans needing it; an unreleased one can come along, one in
	// a later release has to be moved by hand first
	private static string DescribeMissingDependencies( MissingPlanDependenciesException missing )
	{
		IEnumerable<string> plans = missing.MissingDependencies
			.OrderBy( x => x.Key, StringComparer.OrdinalIgnoreCase )
			.Select( x => {
					ProjectPlan dependency = x.Value.Item1;
					ICollection<string> dependants = x.Value.Item2;

					string where = string.IsNullOrEmpty( dependency.Release ) ?
						"unreleased" :
						$"in later release '{dependency.Release}'";

					return
						$"'{x.Key}' ({where}; required by {string.Join( ", ", dependants.Select( d => $"'{d}'" ) )})";
				}
			);

		return "a plan's dependencies must be in its release or an earlier one; missing " +
				string.Join( ", ", plans ) +
				". Include unreleased dependencies in --plans, or add the plan with 'plan set-release --with-dependencies'";
	}
}