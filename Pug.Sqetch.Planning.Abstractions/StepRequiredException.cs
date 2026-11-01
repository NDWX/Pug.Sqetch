namespace Pug.Sqetch.Planning;

public class StepRequiredException( IEnumerable<string> dependants ) : Exception
{
	public IEnumerable<string> Dependants { get; } = dependants;
}