namespace Pug.Sqetch.Deployment;

/// <summary>
/// The rollback asked for does not fit what the database has deployed: a target release outside the
/// bundle's lineage, a plan the latest release does not have, or a plan whose deployed dependants were
/// not asked to go with it. Raised before anything is undone, so the database is unchanged.
/// </summary>
public class RollbackRefusedException
	: DeploymentException
{
	public RollbackRefusedException( string message )
		: base( message )
	{
	}
}
