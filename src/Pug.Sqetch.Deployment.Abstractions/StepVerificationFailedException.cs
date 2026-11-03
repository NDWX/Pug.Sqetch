namespace Pug.Sqetch.Deployment;

/// <summary>
/// A step's verify script raised an error after its deploy script succeeded, so the step failed. It
/// is a <see cref="StepScriptFailedException"/>, since the step failed just the same and is handled
/// the same way.
/// </summary>
public class StepVerificationFailedException
	: StepScriptFailedException
{
	public StepVerificationFailedException( string release, string plan, string step, Exception innerException )
		: base(
			release,
			plan,
			step,
			innerException,
			$"Verification of step '{step}' of plan '{plan}' in release '{release}' failed: {innerException.Message}"
		)
	{
	}
}
