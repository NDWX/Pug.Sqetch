using System.Globalization;
using Pug.Sqetch.Bundling;
using Pug.Sqetch.Deployment;
using Spectre.Console;

namespace Pug.Sqetch.Cli.Commands.Deployment;

/// <summary>
/// Reports deployment progress to the console and, when a log path is given, appends the
/// same lines timestamped to the log file.
/// </summary>
public sealed class ConsoleDeploymentListener : IDeploymentListener, IDisposable
{
	private readonly IAnsiConsole _console;
	private readonly StreamWriter? _log;

	public bool UpToDate { get; private set; }

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
			$"bundle built by {metadata.Bundler} {metadata.BundlerVersion} at "
			+ $"{metadata.Bundled.ToString( "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture )} "
			+ $"(layout {metadata.Layout}, format {metadata.Format})" );

	public void ContinuingFrom( string release ) => Report( $"continuing from {Name( release )}" );

	public void SkippingRelease( string release ) => Report( $"skipping {Name( release )}: already deployed" );

	public void SkippingPlan( string release, string plan ) => Report( $"skipping plan '{plan}' of {Name( release )}: already deployed" );

	public void DeployingRelease( string release ) => Report( $"deploying {Name( release )}" );

	public void DeployingPlan( string release, string plan ) => Report( $"deploying plan '{plan}' of {Name( release )}" );

	public void DeployingStep( string release, string plan, string step ) => Report( $"deploying step '{step}' of plan '{plan}'" );

	public void StepDeployed( string release, string plan, string step ) => Report( $"deployed step '{step}' of plan '{plan}'" );

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
