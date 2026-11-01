using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Planning;

public class AbandonedPlanDependantsException
	: Exception
{
	public List<PlanDependant> Dependants { get; }

	public AbandonedPlanDependantsException( List<PlanDependant> dependants )
	{
		Dependants = dependants;
	}
}