namespace Pug.Sqetch.Planning.Models;

/// <summary>
/// The journaling SQL a project defines, one statement set per slot. Twelve slots name a
/// deployment event, <see cref="GetLatestRelease"/> and <see cref="GetDeployedPlans"/> are the
/// queries a deployment reads its starting point from, and <see cref="PrepareJournal"/> provisions
/// the journal itself; all fifteen are required. The rest are optional
/// (<see cref="JournalingSlots.IsOptional"/>): <see cref="JournalExists"/>, which only commands that
/// must not provision the journal need, and <see cref="AcquireLock"/>/<see cref="ReleaseLock"/>, which
/// guard a database against two deployments at once. Declaration order is the order the CLI lists them in.
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
	JournalExists,

	/// <summary>
	/// Optional query taking the database's deployment lock, run before anything else a deployment
	/// does: one row whose first column is true when the lock was taken, false (or no row) when another
	/// deployment holds it, which refuses the deployment. Meant for a session-level lock — it must
	/// outlive the transaction it is taken in — so it lasts until <see cref="ReleaseLock"/> or the
	/// connection closes.
	/// </summary>
	AcquireLock,

	/// <summary>
	/// Optional statements giving back what <see cref="AcquireLock"/> took, run when a deployment that
	/// took it ends, however it ends.
	/// </summary>
	ReleaseLock
}