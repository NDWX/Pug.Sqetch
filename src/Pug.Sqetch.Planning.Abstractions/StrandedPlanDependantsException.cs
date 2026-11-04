using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Planning;

/// <summary>
/// Moving <see cref="Plan"/> to <see cref="Release"/> would leave <see cref="Dependants"/> in
/// releases before it, depending on a plan that deploys after them.
/// </summary>
public class StrandedPlanDependantsException(
	string plan,
	string release,
	IReadOnlyList<PlanDependant> dependants
)
	: Exception
{
	public string Plan { get; } = plan;

	public string Release { get; } = release;

	public IReadOnlyList<PlanDependant> Dependants { get; } = dependants;
}
