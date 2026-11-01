namespace Pug.Sqetch.Deployment;

/// <summary>A deploy step script failed; the surrounding transaction has been rolled back.</summary>
public class StepScriptFailedException
	: DeploymentException
{
	public string Release { get; }

	public string Plan { get; }

	public string Step { get; }

	public StepScriptFailedException( string release, string plan, string step, Exception innerException )
		: base(
			$"Step '{step}' of plan '{plan}' in release '{release}' failed: {innerException.Message}",
			innerException )
	{
		Release = release;
		Plan = plan;
		Step = step;
	}
}
