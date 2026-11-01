namespace Pug.Sqetch.Deployment;

/// <summary>How often deployment progress is committed to the database.</summary>
public enum DeploymentCommitLevel
{
	/// <summary>Commit after every deployed plan.</summary>
	Plan,

	/// <summary>Commit only after every deployed release.</summary>
	Release
}
