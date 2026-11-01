namespace Pug.Sqetch.Deployment;

/// <summary>The bundle is malformed: content listed in its manifest is missing, or the
/// manifest itself is inconsistent — a release listed twice, a plan referencing a release the
/// manifest does not list, releases that do not form a contiguous lineage. A well-formed
/// bundle that does not fit the database's state is an
/// <see cref="IncompatibleBundleException"/> instead.</summary>
public class InvalidBundleException
	: DeploymentException
{
	public InvalidBundleException( string message )
		: base( message )
	{
	}
}
