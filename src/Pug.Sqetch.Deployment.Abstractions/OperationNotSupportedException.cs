namespace Pug.Sqetch.Deployment;

/// <summary>
/// Thrown when a deployment operation is understood but not implemented — the standalone rollback
/// command today. Kept apart from the failures because retrying can never help.
/// </summary>
public class OperationNotSupportedException
	: DeploymentException
{
	public OperationNotSupportedException( string message )
		: base( message )
	{
	}
}
