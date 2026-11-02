namespace Pug.Sqetch.Deployment.Bundling;

/// <summary>Base class for bundle assembly and writing failures.</summary>
public class BundlingException
	: Exception
{
	public BundlingException( string message )
		: base( message )
	{
	}
}