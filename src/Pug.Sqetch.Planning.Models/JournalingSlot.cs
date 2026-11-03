namespace Pug.Sqetch.Planning.Models;

/// <summary>
/// The journaling SQL a project defines, one statement set per slot. Twelve slots name a
/// deployment event, <see cref="GetLatestRelease"/> and <see cref="GetDeployedPlans"/> are the
/// queries a deployment reads its starting point from, and <see cref="PrepareJournal"/> provisions
/// the journal itself; all fifteen are required. <see cref="JournalExists"/> is optional
/// (<see cref="JournalingSlots.IsOptional"/>): only commands that must not provision the journal
/// need it. Declaration order is the order the CLI lists them in.
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
	GetDeployedPlans,

	/// <summary>
	/// Optional query answering whether the journal exists yet: one row whose first column is true
	/// (or non-zero, or '1/y/yes/t/true') when it does; false or no row when it does not. It lets a
	/// read-only command tell a database never deployed to from a journal query that failed, without
	/// running <see cref="PrepareJournal"/>.
	/// </summary>
	JournalExists
}