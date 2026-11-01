namespace Pug.Sqetch.Planning;

public class MissingStepDependencyException
	: Exception
{
	public string Dependency { get; }

	public MissingStepDependencyException( string dependency )
	{
		Dependency = dependency;
	}
}