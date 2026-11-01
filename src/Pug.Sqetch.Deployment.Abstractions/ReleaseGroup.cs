namespace Pug.Sqetch.Deployment;

/// <summary>
/// A release as the deployment engine deploys it: its plans in deployment order, and the release it
/// follows in the lineage (<paramref name="Dependency"/>, empty for the release that starts it).
/// The engine is handed a list of these, in lineage order, and never learns where they came from.
///
/// The empty <paramref name="Name"/> is the unreleased-plans pseudo-release of test deployments:
/// it may only come last, and nothing may depend on it.
/// </summary>
public sealed record ReleaseGroup(
	string Name,
	string Description,
	string Dependency,
	IReadOnlyList<DeployablePlan> Plans )
{
	public bool HasDependency => !string.IsNullOrEmpty( Dependency );
}

/// <summary>A plan to deploy, its steps in deployment order.</summary>
public sealed record DeployablePlan( string Name, string Description, IReadOnlyList<DeployableStep> Steps );

/// <summary>
/// A step to deploy. Its scripts are not carried here: the engine asks an
/// <see cref="IScriptContentReader"/> for them when it reaches the step.
/// </summary>
public sealed record DeployableStep( string Name, string Description );
