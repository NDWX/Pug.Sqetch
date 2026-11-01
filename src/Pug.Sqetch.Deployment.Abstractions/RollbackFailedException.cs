namespace Pug.Sqetch.Deployment;

/// <summary>
/// Thrown when a rollback script fails. The database is then partially compensated — some of the
/// deployment undone and some still in place — which is worse than either end state, so this is
/// deliberately louder than the failure that triggered the rollback and replaces it: the run that
/// asked for the rollback has already reported why it started.
/// </summary>
public class RollbackFailedException
	: DeploymentException
{
	public string Release { get; }

	public string Plan { get; }

	public string Step { get; }

	public RollbackFailedException( string release, string plan, string step, Exception innerException )
		: base(
			$"Rolling back step '{step}' of plan '{plan}' in release '{release}' failed; the database is "
			+ "partially rolled back and needs attention.",
			innerException )
	{
		Release = release;
		Plan = plan;
		Step = step;
	}
}
