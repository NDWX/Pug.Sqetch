namespace Pug.Sqetch.Planning;

/// <summary>
/// Refuses a dependency change that would make <see cref="Plan"/> depend on itself. <see cref="Path"/>
/// runs from the new dependency through each plan depending on the next, ending at <see cref="Plan"/>.
/// </summary>
public class PlanDependencyCycleException : Exception
{
	public string Plan { get; }

	public IReadOnlyList<string> Path { get; }

	public PlanDependencyCycleException( string plan, IReadOnlyList<string> path )
		: base( $"Plan '{plan}' would depend on itself through {string.Join( " -> ", path )}." )
	{
		Plan = plan;
		Path = path;
	}
}
