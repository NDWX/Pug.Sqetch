namespace Pug.Sqetch.Deployment;

/// <summary>
/// The database's deployment lock is held — the journaling SQL's <c>AcquireLock</c> said so — so this
/// deployment was refused before it touched anything. Another deployment is running against the same
/// database; retrying once it has finished is safe.
/// </summary>
public class DeploymentLockedException
	: DeploymentException
{
	public DeploymentLockedException()
		: base( "Another deployment holds this database's deployment lock; retry when it has finished." )
	{
	}
}
