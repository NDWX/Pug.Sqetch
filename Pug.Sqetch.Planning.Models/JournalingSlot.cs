namespace Pug.Sqetch.Planning.Models;

/// <summary>
/// The journaling SQL a project must define, one statement set per slot. Twelve slots name a
/// deployment event, <see cref="GetLatestRelease"/> and <see cref="GetDeployedPlans"/> are the
/// queries a deployment reads its starting point from, and <see cref="PrepareJournal"/> provisions
/// the journal itself. Declaration order is the order the CLI lists them in.
/// </summary>
public enum JournalingSlot
{
	DeployingRelease,
	DeployingPlan,
	DeployingStep,
	StepDeployed,
	PlanDeployed,
	ReleaseDeployed,
	RollingBackRelease,
	RollingBackPlan,
	RollingBackStep,
	RolledBackStep,
	RolledBackPlan,
	RolledBackRelease,
	PrepareJournal,
	GetLatestRelease,
	GetDeployedPlans
}
