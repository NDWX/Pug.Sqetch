namespace Pug.Sqetch.Deployment;

/// <summary>
/// Where the deployment engine gets a step's SQL from. The engine knows plans and steps by name
/// only, so whatever holds the scripts — a bundle, a project directory, a database of its own —
/// is the implementer's business; the release is passed too, for a source that keeps scripts per
/// release.
///
/// A script that cannot be read should throw. While deploying, the exception reaches the caller as
/// thrown and the open transaction is rolled back like on any other failure; while rolling back it
/// becomes a <see cref="RollbackFailedException"/>, since compensation stopped partway.
/// </summary>
public interface IScriptContentReader
{
	/// <summary>The SQL that deploys <paramref name="step"/> of <paramref name="plan"/>.</summary>
	string ReadDeployScript( string release, string plan, string step );

	/// <summary>
	/// The SQL that checks what the deploy script of the same step did, run right after it in the same
	/// transaction. It fails the step only by raising an error; a blank script is not run.
	/// </summary>
	string ReadVerifyScript( string release, string plan, string step );

	/// <summary>The SQL that undoes what the deploy script of the same step did.</summary>
	string ReadRollbackScript( string release, string plan, string step );
}