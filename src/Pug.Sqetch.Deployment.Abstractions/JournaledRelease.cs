namespace Pug.Sqetch.Deployment;

/// <summary>
/// The release the change journal is at, and whether it finished deploying.
/// <paramref name="Name"/> is empty for the unreleased-plans pseudo-release of test bundles.
/// <paramref name="Completed"/> is true only when the release's completion was journaled by the
/// project's ReleaseDeployed statement; false means plans of that release
/// remain to deploy, so they must deploy before any later release — a bundle that does not
/// itself contain the release cannot be deployed.
/// </summary>
public sealed record JournaledRelease( string Name, bool Completed );
