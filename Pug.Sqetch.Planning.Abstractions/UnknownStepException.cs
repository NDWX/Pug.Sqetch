namespace Pug.Sqetch.Planning;

public class UnknownStepException
	: Exception
{
	public string Plan { get; }

	public string Name { get; }

	public UnknownStepException( string plan, string name )
		: base( $"Step '{name}' does not exist in plan '{plan}'." )
	{
		Plan = plan;
		Name = name;
	}
}
