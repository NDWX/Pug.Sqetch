using System.Globalization;

using Pug.Sqetch.Cli;
using Pug.Sqetch.Deployment.Bundling;

using Spectre.Console;

namespace Pug.Sqetch.Deployment.Cli.Commands;

/// <summary>
/// Reports deployment progress to the console and, when a log path is given, appends the
/// same lines timestamped to the log file.
/// </summary>
public sealed class ConsoleDeploymentListener : IDeploymentListener, IDisposable
{
	private readonly IAnsiConsole _console;
	private readonly StreamWriter? _log;
	private readonly HashSet<string> _warned = [ ];
	private IReadOnlySet<string> _unfinalized = new HashSet<string>();
	private WarningWriter? _warnings;

	public bool UpToDate { get; private set; }

	/// <summary>Whether a rollback found nothing deployed to undo.</summary>
	public bool NothingRolledBack { get; private set; }

	/// <summary>
	/// Whether a transaction of this run committed and was not taken back by a compensating
	/// rollback — the difference between a failed run a pipeline may retry blindly and one that
	/// needs looking at first. Preparing the journal does not count: its DDL is the maintainer's
	/// and contracted to be safe to re-run, so a run that got no further left nothing to undo.
	/// </summary>
	public bool DatabaseChanged { get; private set; }

	public ConsoleDeploymentListener( IAnsiConsole console, string? logPath )
	{
		_console = console;

		if( logPath is null )
			return;

		string? parent = Path.GetDirectoryName( logPath );

		if( !string.IsNullOrEmpty( parent ) )
			Directory.CreateDirectory( parent );

		_log = new StreamWriter( logPath, append: true );
	}

	/// <summary>
	/// Says who built the bundle about to be deployed and when. Not part of
	/// <see cref="IDeploymentListener"/>: the engine is handed a bundle already read and has no use
	/// for it, so only the command that read it reports it.
	/// </summary>
	public void BundleRead( BundleMetadata metadata )
		=> Report(
			$"bundle built by {metadata.Bundler} {metadata.BundlerVersion} at " +
			$"{metadata.Bundled.ToString( "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture )} " +
			$"(layout {metadata.Layout}, format {metadata.Format})"
		);

	/// <summary>
	/// Says verification is off for this run (<c>--no-verify</c>), so the console and the log show
	/// that the steps deployed were not checked. Not part of <see cref="IDeploymentListener"/>: the
	/// command decides it, and the engine just runs no verify scripts.
	/// </summary>
	public void VerificationSkipped() => Report( "step verification skipped (--no-verify)" );

	/// <summary>
	/// Names the releases of the bundle that are not finalized (the unreleased-plans pseudo-release
	/// is ""), so rolling one back warns that its rollback scripts may differ from what was
	/// deployed. Not part of <see cref="IDeploymentListener"/>: only the rollback command knows
	/// that the scripts it runs are not the ones that deployed.
	/// </summary>
	public void WarnWhenRollingBack( IEnumerable<string> unfinalized, WarningWriter warnings )
	{
		_unfinalized = unfinalized.ToHashSet();
		_warnings = warnings;
	}

	public void ContinuingFrom( string release ) => Report( $"continuing from {Name( release )}" );

	public void SkippingRelease( string release ) => Report( $"skipping {Name( release )}: already deployed" );

	public void SkippingPlan( string release, string plan ) =>
		Report( $"skipping plan '{plan}' of {Name( release )}: already deployed" );

	public void DeployingRelease( string release ) => Report( $"deploying {Name( release )}" );

	public void DeployingPlan( string release, string plan ) =>
		Report( $"deploying plan '{plan}' of {Name( release )}" );

	public void DeployingStep( string release, string plan, string step ) =>
		Report( $"deploying step '{step}' of plan '{plan}'" );

	public void LockAcquired() => Report( "acquired the deployment lock" );

	public void LockNotReleased( Exception exception )
		=> Report(
			$"warning: the deployment lock could not be released ({exception.Message}); it ends with this " +
			"database session, when sqetch-deploy exits"
		);

	public void VerifyingStep( string release, string plan, string step ) =>
		Report( $"verifying step '{step}' of plan '{plan}'" );

	public void StepDeployed( string release, string plan, string step ) =>
		Report( $"deployed step '{step}' of plan '{plan}'" );

	public void PlanDeployed( string release, string plan ) => Report( $"deployed plan '{plan}' of {Name( release )}" );

	public void ReleaseDeployed( string release ) => Report( $"deployed {Name( release )}" );

	public void Committed()
	{
		DatabaseChanged = true;

		Report( "committed" );
	}

	public void NothingToDeploy()
	{
		UpToDate = true;

		Report( "nothing to deploy" );
	}

	public void RollingBack( string reason ) => Report( $"rolling back: {reason}" );

	public void RollingBackPlan( string release, string plan )
		=> Report( $"rolling back plan '{plan}' of {Name( release )}" );

	public void RollingBackRelease( string release )
	{
		Report( $"rolling back {Name( release )}" );

		if( _warnings is null || !_unfinalized.Contains( release ) || !_warned.Add( release ) )
			return;

		string warning =
			$"{Name( release )} is not finalized in this bundle; its rollback scripts may differ from what was deployed";

		_warnings.Write( warning );

		Log( $"warning: {warning}" );
	}

	public void NothingToRollBack()
	{
		NothingRolledBack = true;

		Report( "nothing to roll back" );
	}

	public void RolledBack()
	{
		// compensation ran to the end, so the database is as it was before the run
		DatabaseChanged = false;

		Report( "rolled back" );
	}

	/// <summary>Logged only; the console line is the command's own error report.</summary>
	public void Failed( string message ) => Log( $"failed: {message}" );

	public void Dispose() => _log?.Dispose();

	private static string Name( string release ) => release.Length == 0 ? "unreleased plans" : $"release '{release}'";

	private void Report( string line )
	{
		_console.WriteLine( line );

		Log( line );
	}

	private void Log( string line ) => _log?.WriteLine( $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}" );
}