namespace Pug.Sqetch.Deployment;

/// <summary>
/// When a deployment should undo itself. Rollback is a <em>compensating</em> pass — it runs the
/// rollback script of everything this run committed, in reverse — so it is independent of
/// <see cref="DeploymentCommitLevel"/>: at plan level the earlier plans are already durable, and
/// no database transaction could take them back.
/// </summary>
public enum RollbackTrigger
{
	/// <summary>Leave whatever committed in place; a failure rolls back only its open transaction.</summary>
	None,

	/// <summary>On failure, undo everything this run had already committed, then report the failure.</summary>
	OnError,

	/// <summary>
	/// Undo a deployment that succeeded, leaving the database as it was. For proving a bundle
	/// applies cleanly — most often a test bundle, though what a caller wants to prove and then undo
	/// is its own business, so a bundle of finalized releases is not refused.
	/// </summary>
	OnSuccess
}
