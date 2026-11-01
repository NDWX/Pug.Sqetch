namespace Pug.Sqetch.Deployment;

/// <summary>
/// The bundle is well formed but does not fit the database's current state: it neither
/// contains the journaled release nor continues from a release the database has completely
/// deployed. The message carries the remedy, so the engine supplies the wording.
/// </summary>
public class IncompatibleBundleException
	: DeploymentException
{
	/// <summary>The release the journal is at; null when the database has nothing journaled.</summary>
	public JournaledRelease? Journaled { get; }

	public IncompatibleBundleException( JournaledRelease? journaled, string message )
		: base( message )
	{
		Journaled = journaled;
	}
}
