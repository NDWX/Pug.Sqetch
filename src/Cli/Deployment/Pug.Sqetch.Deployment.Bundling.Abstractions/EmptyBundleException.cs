namespace Pug.Sqetch.Deployment.Bundling;

/// <summary>Thrown when no plans match the bundle selection.</summary>
public class EmptyBundleException
	: BundlingException
{
	public EmptyBundleException()
		: base( "No plans match the selection." )
	{
	}
}