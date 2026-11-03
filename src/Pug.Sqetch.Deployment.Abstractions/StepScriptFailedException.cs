namespace Pug.Sqetch.Deployment;

/// <summary>
/// A deploy step script failed; the surrounding transaction has been rolled back, which on an engine
/// that commits DDL implicitly may not have taken the script's schema changes back.
/// </summary>
public class StepScriptFailedException
	: DeploymentException
{
	public string Release { get; }

	public string Plan { get; }

	public string Step { get; }

	public StepScriptFailedException( string release, string plan, string step, Exception innerException )
		: this(
			release,
			plan,
			step,
			innerException,
			$"Step '{step}' of plan '{plan}' in release '{release}' failed: {innerException.Message}"
		)
	{
	}

	protected StepScriptFailedException(
		string release,
		string plan,
		string step,
		Exception innerException,
		string message
	)
		: base( message, innerException )
	{
		Release = release;
		Plan = plan;
		Step = step;
	}
}