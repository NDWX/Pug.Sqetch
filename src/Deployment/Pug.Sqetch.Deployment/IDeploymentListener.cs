namespace Pug.Sqetch.Deployment;

/// <summary>
/// Progress callbacks of a deployment, for the host to report to the user. The empty
/// release name stands for the unreleased-plans pseudo-release of test bundles.
/// </summary>
public interface IDeploymentListener
{
	/// <summary>
	/// Fired once when the bundle does not contain the journaled release but continues from
	/// it: nothing is skipped in that case, so this is the only indication of what the
	/// deployment builds on.
	/// </summary>
	void ContinuingFrom( string release );

	void SkippingRelease( string release );

	void SkippingPlan( string release, string plan );

	void DeployingRelease( string release );

	void DeployingPlan( string release, string plan );

	void DeployingStep( string release, string plan, string step );

	void StepDeployed( string release, string plan, string step );

	void PlanDeployed( string release, string plan );

	void ReleaseDeployed( string release );

	void Committed();

	void NothingToDeploy();

	/// <summary>
	/// Fired once when a compensating rollback begins, saying why. Rollback undoes what the run
	/// already committed, so an operator watching needs to know it started and that what follows
	/// is undoing rather than deploying.
	/// </summary>
	void RollingBack( string reason );

	void RollingBackPlan( string release, string plan );

	/// <summary>Fired once when every deployed plan has been rolled back.</summary>
	void RolledBack();
}
