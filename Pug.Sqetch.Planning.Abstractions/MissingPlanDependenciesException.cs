using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Planning;

public class MissingPlanDependenciesException(
	IDictionary<string, (ProjectPlan, ICollection<string>)> missingDependencies
)
	: Exception
{
	public IDictionary<string, (ProjectPlan, ICollection<string>)> MissingDependencies { get; } = missingDependencies;
}