namespace Pug.Sqetch.Deployment;

/// <summary>Base class for deployment failures.</summary>
public class DeploymentException
	: Exception
{
	public DeploymentException( string message )
		: base( message )
	{
	}

	public DeploymentException( string message, Exception innerException )
		: base( message, innerException )
	{
	}
}
