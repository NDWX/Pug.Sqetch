namespace Pug.Sqetch.Deployment.Bundling;

/// <summary>The bundle's manifest is missing, unreadable or structurally invalid.</summary>
public class InvalidBundleManifestException
	: BundlingException
{
	public InvalidBundleManifestException( string message )
		: base( message )
	{
	}
}