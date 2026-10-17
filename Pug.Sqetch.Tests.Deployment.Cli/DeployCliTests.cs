using System.Text;
using System.Text.RegularExpressions;
using Pug.Sqetch.Bundling;
using Pug.Sqetch.Bundling.BundleTypes;
using Pug.Sqetch.Bundling.Layouts;
using Pug.Sqetch.Cli.Commands.Deployment;
using Pug.Sqetch.Deployment;
using Pug.Sqetch.Deployment.Cli;
using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Models;
using Spectre.Console.Cli.Testing;

namespace Pug.Sqetch.Tests.Deployment.Cli;

/// <summary>
/// Drives 'sqetch-deploy' through <see cref="CommandAppTester"/> hosting the production
/// configuration, with a fake driver and journal writer registered in the registrar.
/// Bundles are written to temp directories and passed by absolute path, so no test changes
/// the process-wide current directory.
/// </summary>
public class DeployCliTests : IDisposable
{
	private static readonly DateTime Bundled = new ( 2026, 10, 17, 3, 15, 42, DateTimeKind.Utc );

	private readonly string _root;
	private readonly FakeDatabaseDriver _driver = new ();
	private readonly FakeJournal _journal = new ();
	private readonly FakeDriverFactory _factory;
	private readonly DatabaseDriverRegistry _drivers = new ();

	public DeployCliTests()
	{
		_root = Path.Combine( Path.GetTempPath(), "sqetch-deploy-cli-tests", Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( _root );

		_factory = new FakeDriverFactory( _driver );

		_drivers.Register( _factory.Name, () => _factory );
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete( _root, recursive: true );
		}
		catch( IOException )
		{
		}
	}

	[Fact]
	public void DeploysAZipBundlePassingDriverParametersAndTimeout()
	{
		string bundle = WriteBundle( "demo.zip" );

		CommandAppResult result = Run(
			"deploy", bundle, "--driver", "pg", "--pg-host", "db1", "--pg-database", "app", "--timeout", "60" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Contains( "deployment complete", result.Output );
		Assert.Contains(
			"bundle built by sqetch-deploy 0.1.0 at 2026-10-17T03:15:42Z (layout default, format 1)",
			result.Output );

		Assert.Equal( TimeSpan.FromSeconds( 60 ), _factory.ReceivedTimeout );
		Assert.Equal( "db1", _factory.ReceivedParameters!["host"] );
		Assert.Equal( "app", _factory.ReceivedParameters["database"] );

		// #1 prepares the journal and #2 reads it, so the deployment's own transaction is #3
		Assert.Equal(
			["script #3 create table t ()", "script #3 create index i"],
			_driver.Events.Where( x => x.StartsWith( "script" ) ) );
		Assert.Equal( ["ReleaseDeployed 2026.07"], _driver.Transactions[2].Journaled.Where( x => x.StartsWith( "ReleaseDeployed" ) ) );
		// the driver owns the connection, so the command releases it once — after the last commit
		Assert.Equal( ["commit #3", "dispose"], _driver.Events.TakeLast( 2 ) );
		Assert.True( _driver.Disposed );
	}

	[Fact]
	public void EqualsSyntaxBindsDriverParameterValues()
	{
		CommandAppResult result = Run(
			"deploy", WriteBundle( "demo.zip" ), "--driver", "pg", "--pg-connection-string=Server=x;Db=y" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Equal( "Server=x;Db=y", _factory.ReceivedParameters!["connection-string"] );
	}

	[Fact]
	public void UndefinedDriverParameterListsTheAvailableOnes()
	{
		CommandAppResult result = Run(
			"deploy", WriteBundle( "demo.zip" ), "--driver", "pg", "--pg-bogus", "x", "--pg-host", "db1", "--pg-database", "app" );

		Assert.Equal( DeployExitCodes.UsageError, result.ExitCode );
		Assert.Contains( "no parameter 'bogus'", result.Output );
		Assert.Contains( "--pg-host", result.Output );
	}

	[Fact]
	public void OptionsOutsideTheDriverPrefixAreRejected()
	{
		CommandAppResult result = Run(
			"deploy", WriteBundle( "demo.zip" ), "--driver", "pg", "--nope", "x" );

		Assert.Equal( DeployExitCodes.UsageError, result.ExitCode );
		Assert.Contains( "unknown option '--nope'", result.Output );
	}

	[Fact]
	public void MissingRequiredParameterGroupingListsTheAlternatives()
	{
		CommandAppResult result = Run(
			"deploy", WriteBundle( "demo.zip" ), "--driver", "pg", "--pg-host", "db1" );

		Assert.Equal( DeployExitCodes.UsageError, result.ExitCode );
		Assert.Contains( "(--pg-host, --pg-database) or (--pg-connection-string)", result.Output );
	}

	[Fact]
	public void RepeatedDriverParametersAreRejected()
	{
		CommandAppResult result = Run(
			"deploy", WriteBundle( "demo.zip" ), "--driver", "pg", "--pg-host", "a", "--pg-host", "b" );

		Assert.Equal( DeployExitCodes.UsageError, result.ExitCode );
		Assert.Contains( "more than once", result.Output );
	}

	[Fact]
	public void UnknownDriverListsTheRegisteredOnes()
	{
		CommandAppResult result = Run( "deploy", WriteBundle( "demo.zip" ), "--driver", "oracle" );

		Assert.Equal( DeployExitCodes.UnknownDriver, result.ExitCode );
		Assert.Contains( "unknown database driver 'oracle'", result.Output );
		Assert.Contains( "pg", result.Output );
	}

	[Fact]
	public void DriverCreationFailureIsReported()
	{
		_factory.FailWith = new InvalidOperationException( "bad credentials" );

		CommandAppResult result = Run(
			"deploy", WriteBundle( "demo.zip" ), "--driver", "pg", "--pg-host", "db1", "--pg-database", "app" );

		Assert.Equal( DeployExitCodes.DriverCreationFailed, result.ExitCode );
		Assert.Contains( "failed to create database driver 'pg': bad credentials", result.Output );
	}

	/// <summary>
	/// The journal is the project's own SQL now and travels in the bundle, so there is nothing to
	/// select. A pipeline still passing --journal must be told, not silently ignored — which is what
	/// would happen if the option were merely unused.
	/// </summary>
	[Fact]
	public void TheRetiredJournalSwitchIsRejected()
	{
		CommandAppResult result = Run(
			"deploy", WriteBundle( "demo.zip" ), "--driver", "pg", "--pg-host", "h", "--pg-database", "d",
			"--journal", "table" );

		Assert.Equal( DeployExitCodes.UsageError, result.ExitCode );

		// rejected by the driver-parameter parser: every unrecognized option must carry the driver's
		// own '--pg-' prefix, so a stale '--journal' is named rather than quietly ignored
		string message = Regex.Replace( result.Output, @"\s+", " " );

		Assert.Contains( "--journal", message );
		Assert.Contains( "--pg-", message );
	}

	[Fact]
	public void TheRollbackModeIsValidated()
	{
		CommandAppResult result = Run(
			"deploy", WriteBundle( "demo.zip" ), "--driver", "pg", "--pg-host", "h", "--pg-database", "d",
			"--rollback", "sometimes" );

		Assert.Equal( DeployExitCodes.UsageError, result.ExitCode );
		Assert.Contains( "--rollback", result.Output );
	}

	/// <summary>
	/// The mode is usually pointed at a test bundle, but what a caller wants to prove and then undo
	/// is its own business: a bundle of finalized releases deploys and is compensated like any other,
	/// and the run reports success because the deployment it was asked to prove worked.
	/// </summary>
	[Fact]
	public void RollingBackOnSuccessUndoesAFinalizedBundleToo()
	{
		CommandAppResult result = Run(
			"deploy", WriteBundle( "demo.zip" ), "--driver", "pg", "--pg-host", "h", "--pg-database", "d",
			"--rollback", "on-success" );

		Assert.Equal( 0, result.ExitCode );

		Assert.Equal(
			["create table t ()", "create index i", "-- r", "-- r"],
			_driver.AppliedScripts );
	}

	[Fact]
	public void BundleMissingItsManifestOrAScriptFails()
	{
		string empty = Path.Combine( _root, "empty" );
		Directory.CreateDirectory( empty );
		File.WriteAllText( Path.Combine( empty, "readme.txt" ), "no manifest here" );
		File.WriteAllText(
			Path.Combine( empty, BundleMetadata.EntryName ),
			new BundleMetadata( 1, "default", "sqetch-deploy", "0.1.0", Bundled ).ToText() );

		CommandAppResult noManifest = Run( "deploy", empty, "--driver", "pg", "--pg-host", "h", "--pg-database", "d" );

		Assert.Equal( DeployExitCodes.InvalidBundle, noManifest.ExitCode );
		Assert.Contains( "manifest.json", noManifest.Output );

		string incomplete = WriteBundle( "incomplete.zip", withRollbackScripts: false );

		CommandAppResult missingScript = Run( "deploy", incomplete, "--driver", "pg", "--pg-host", "h", "--pg-database", "d" );

		Assert.Equal( DeployExitCodes.InvalidBundle, missingScript.ExitCode );
		Assert.Contains( "rollback.sql", missingScript.Output );
		Assert.Empty( _driver.Events );
	}

	[Fact]
	public void BundleReadLineNamesTheBuilderAndGoesToTheLogToo()
	{
		string log = Path.Combine( _root, "logs", "built.log" );

		CommandAppResult result = Run(
			"deploy", WriteBundle( "demo.zip" ), "--driver", "pg", "--pg-host", "h", "--pg-database", "d", "--log", log );

		Assert.Equal( 0, result.ExitCode );
		Assert.Contains(
			"bundle built by sqetch-deploy 0.1.0 at 2026-10-17T03:15:42Z (layout default, format 1)",
			File.ReadAllLines( log )[0] );
	}

	[Fact]
	public void BundleMissingItsMetadataIsInvalidAndNothingIsTouched()
	{
		string bundle = WriteBundle( "no-metadata", DirectoryBundleType.TypeName );

		File.Delete( Path.Combine( bundle, BundleMetadata.EntryName ) );

		CommandAppResult result = Run( "deploy", bundle, "--driver", "pg", "--pg-host", "h", "--pg-database", "d" );

		Assert.Equal( DeployExitCodes.InvalidBundle, result.ExitCode );
		Assert.Contains( BundleMetadata.EntryName, result.Output );
		Assert.Empty( _driver.Events );
	}

	[Fact]
	public void BundleWithMalformedMetadataIsInvalid()
	{
		string bundle = WriteBundle( "bad-metadata", DirectoryBundleType.TypeName );

		File.WriteAllText( Path.Combine( bundle, BundleMetadata.EntryName ), "format=1\nthis is not a pair\n" );

		CommandAppResult result = Run( "deploy", bundle, "--driver", "pg", "--pg-host", "h", "--pg-database", "d" );

		Assert.Equal( DeployExitCodes.InvalidBundle, result.ExitCode );
		Assert.Contains( "line 2", result.Output );
		Assert.Empty( _driver.Events );
	}

	/// <summary>
	/// A layout the registry lacks is not a malformed bundle and not the host's own mistake: the
	/// bundle was made by something newer, and the pipeline's remedy is a newer sqetch-deploy.
	/// </summary>
	[Fact]
	public void BundleOfAnUnknownLayoutNeedsANewerTool()
	{
		string bundle = WriteBundle( "alien", DirectoryBundleType.TypeName );
		string file = Path.Combine( bundle, BundleMetadata.EntryName );

		File.WriteAllText( file, File.ReadAllText( file ).Replace( "layout=default", "layout=quantum" ) );

		CommandAppResult result = Run( "deploy", bundle, "--driver", "pg", "--pg-host", "h", "--pg-database", "d" );

		Assert.Equal( DeployExitCodes.UnsupportedBundle, result.ExitCode );

		string message = Regex.Replace( result.Output, @"\s+", " " );

		Assert.Contains( "layout 'quantum'", message );
		Assert.Contains( "known: default", message );
		Assert.Contains( "upgrade", message );
		Assert.Empty( _driver.Events );
	}

	[Fact]
	public void BundleOfANewerFormatNeedsANewerTool()
	{
		string bundle = WriteBundle( "future", DirectoryBundleType.TypeName );
		string file = Path.Combine( bundle, BundleMetadata.EntryName );

		File.WriteAllText( file, File.ReadAllText( file ).Replace( "format=1", "format=2" ) );

		CommandAppResult result = Run( "deploy", bundle, "--driver", "pg", "--pg-host", "h", "--pg-database", "d" );

		Assert.Equal( DeployExitCodes.UnsupportedBundle, result.ExitCode );

		string message = Regex.Replace( result.Output, @"\s+", " " );

		Assert.Contains( "format 2 of layout 'default'", message );
		Assert.Contains( "reads up to format 1", message );
		Assert.Empty( _driver.Events );
	}

	[Fact]
	public void LogFileReceivesTimestampedProgressLines()
	{
		string log = Path.Combine( _root, "logs", "deploy.log" );

		CommandAppResult result = Run(
			"deploy", WriteBundle( "demo.zip" ), "--driver", "pg", "--pg-host", "h", "--pg-database", "d", "--log", log );

		Assert.Equal( 0, result.ExitCode );

		string[] lines = File.ReadAllLines( log );

		Assert.All( lines, line => Assert.Matches( @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}  ", line ) );
		Assert.Contains( lines, line => line.EndsWith( "deploying plan 'table' of release '2026.07'" ) );
		Assert.Contains( lines, line => line.EndsWith( "committed" ) );
	}

	[Fact]
	public void InvalidSwitchValuesAreRejected()
	{
		string bundle = WriteBundle( "demo.zip" );

		int usage = DeployExitCodes.UsageError;

		Assert.Equal( usage, Run( "deploy", bundle, "--driver", "pg", "--commit-level", "bogus" ).ExitCode );
		Assert.Equal( usage, Run( "deploy", bundle, "--driver", "pg", "--timeout", "-5" ).ExitCode );
		Assert.Equal( usage, Run( "deploy", bundle ).ExitCode );
		Assert.Equal( usage, Run( "deploy", bundle, "--driver", "pg", "--bundle-type", "rar" ).ExitCode );
	}

	[Fact]
	public void BundleTypeIsInferredFromThePath()
	{
		string directory = WriteBundle( "bundle-dir", DirectoryBundleType.TypeName );

		Assert.Equal(
			0,
			Run( "deploy", directory, "--driver", "pg", "--pg-host", "h", "--pg-database", "d" ).ExitCode );

		string targz = WriteBundle( "demo.tar.gz", TarGzBundleType.TypeName );

		// the two bundles hold the same release, so the second is a deployment in its own right
		// only against a database that has not seen the first
		_journal.Latest = null;
		_journal.DeployedPlans.Clear();
		_journal.StartedReleases.Clear();

		Assert.Equal(
			0,
			Run( "deploy", targz, "--driver", "pg", "--pg-host", "h", "--pg-database", "d" ).ExitCode );
	}

	[Fact]
	public void UpToDateDatabaseReportsSoAndTouchesNothing()
	{
		_journal.Latest = new JournaledRelease( "2026.07", Completed: true );
		_journal.DeployedPlans["2026.07"] = ["table", "index"];

		CommandAppResult result = Run(
			"deploy", WriteBundle( "demo.zip" ), "--driver", "pg", "--pg-host", "h", "--pg-database", "d" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Contains( "database is up to date", result.Output );

		// the journal is prepared and read on every deployment, so those two transactions are
		// expected; nothing beyond them ran
		Assert.Equal( 2, _driver.Transactions.Count );
		Assert.Empty( _driver.AppliedScripts );
	}

	[Fact]
	public void ContinuationBundleIsRefusedWhenTheJournaledPredecessorIsIncomplete()
	{
		_journal.Latest = new JournaledRelease( "2026.06", Completed: false );

		string bundle = WriteBundle( "continuation.zip", dependency: "2026.06" );

		CommandAppResult result = Run( "deploy", bundle, "--driver", "pg", "--pg-host", "h", "--pg-database", "d" );

		Assert.Equal( DeployExitCodes.IncompatibleBundle, result.ExitCode );

		// console wrapping breaks the message across lines
		string message = Regex.Replace( result.Output, @"\s+", " " );

		Assert.Contains( "release '2026.06' is only partially deployed", message );
		Assert.Contains( "before release '2026.07'", message );

		// refused from what the journal said, so the journal was prepared and read — and no more
		Assert.Equal( 2, _driver.Transactions.Count );
		Assert.Empty( _driver.AppliedScripts );
	}

	[Fact]
	public void RollbackParsesItsSwitchesButIsNotYetSupported()
	{
		string bundle = WriteBundle( "demo.zip" );

		CommandAppResult result = Run(
			"rollback", bundle, "--driver", "pg", "--pg-host", "h", "--pg-database", "d", "--until-release", "2026.07" );

		Assert.Equal( DeployExitCodes.NotSupported, result.ExitCode );
		Assert.Contains( "rollback is not yet supported", result.Output );

		CommandAppResult both = Run(
			"rollback", bundle, "--driver", "pg", "--after-release", "a", "--until-release", "b" );

		Assert.Equal( DeployExitCodes.UsageError, both.ExitCode );
		Assert.Contains( "not both", both.Output );

		CommandAppResult neither = Run( "rollback", bundle, "--driver", "pg" );

		Assert.Equal( DeployExitCodes.UsageError, neither.ExitCode );
		Assert.Contains( "--after-release or --until-release", neither.Output );
	}

	[Fact]
	public void AMissingBundleIsItsOwnFailure()
	{
		CommandAppResult result = Run(
			"deploy", Path.Combine( _root, "never-built.zip" ), "--driver", "pg", "--pg-host", "h",
			"--pg-database", "d" );

		Assert.Equal( DeployExitCodes.BundleNotFound, result.ExitCode );
		Assert.Contains( "does not exist", result.Output );

		// a bundle that is not there is refused before a driver is ever created
		Assert.Empty( _driver.Events );
	}

	/// <summary>
	/// The three journal codes are separated because their remedies are: provisioning DDL that will
	/// not run, a query whose result the deployment cannot read, and a write statement that fails.
	/// </summary>
	[Fact]
	public void JournalingFailuresAreToldApartByTheirSlot()
	{
		_driver.FailingJournalingStatements.Add( FakeJournal.Marker( JournalingSlot.PrepareJournal ) );

		Assert.Equal( DeployExitCodes.JournalPreparationFailed, Deploy( "prepare.zip" ).ExitCode );

		_driver.FailingJournalingStatements.Clear();
		_driver.FailingJournalingQueries.Add( FakeJournal.Marker( JournalingSlot.GetLatestRelease ) );

		Assert.Equal( DeployExitCodes.JournalQueryFailed, Deploy( "query.zip" ).ExitCode );

		_driver.FailingJournalingQueries.Clear();
		_driver.FailingJournalingStatements.Add( FakeJournal.Marker( JournalingSlot.DeployingRelease ) );

		Assert.Equal( DeployExitCodes.JournalWriteFailed, Deploy( "write.zip" ).ExitCode );
	}

	/// <summary>
	/// Whether anything committed is the one thing a pipeline cannot work out from the message, and
	/// it decides whether the run may be retried as it stands. At release commit level the first
	/// plan's failure takes the whole release back with it; at plan level it does not.
	/// </summary>
	[Fact]
	public void AFailedDeploymentSaysWhetherTheDatabaseChanged()
	{
		_driver.FailingScripts.Add( "create table t ()" );

		CommandAppResult untouched = Deploy( "untouched.zip" );

		Assert.Equal( DeployExitCodes.DeploymentFailed, untouched.ExitCode );
		Assert.Empty( _driver.AppliedScripts );

		_driver.FailingScripts.Clear();
		_driver.FailingScripts.Add( "create index i" );

		CommandAppResult partway = Deploy( "partway.zip", "--commit-level", "plan" );

		Assert.Equal( DeployExitCodes.DeploymentFailedPartway, partway.ExitCode );
		Assert.Equal( ["create table t ()"], _driver.AppliedScripts );
	}

	/// <summary>
	/// A successful compensation puts the database back, so the run reports the plain deployment
	/// failure rather than the partway one — the code is about what was left behind, not about how
	/// much ran.
	/// </summary>
	[Fact]
	public void RollingBackOnErrorRestoresThePlainFailureCode()
	{
		_driver.FailingScripts.Add( "create index i" );

		CommandAppResult result = Deploy(
			"compensated.zip", "--commit-level", "plan", "--rollback", "on-error" );

		Assert.Equal( DeployExitCodes.DeploymentFailed, result.ExitCode );
		Assert.Equal( ["create table t ()", "-- r"], _driver.AppliedScripts );
	}

	[Fact]
	public void AFailedRollbackOutranksTheFailureThatStartedIt()
	{
		_driver.FailingScripts.Add( "create index i" );
		_driver.FailingScripts.Add( "-- r" );

		CommandAppResult result = Deploy(
			"half-compensated.zip", "--commit-level", "plan", "--rollback", "on-error" );

		Assert.Equal( DeployExitCodes.RollbackFailed, result.ExitCode );
		Assert.Contains( "partially rolled back", Regex.Replace( result.Output, @"\s+", " " ) );
	}

	/// <summary>
	/// A driver is configured without connecting, so an unreachable database is not a creation
	/// failure and does not surface until the first transaction. The fake driver is always reachable,
	/// so the arm is pinned here and the behaviour itself in the provider's own tests.
	/// </summary>
	[Fact]
	public void AnUnreachableDatabaseIsNotADriverCreationFailure()
	{
		Assert.Equal(
			DeployExitCodes.DatabaseUnreachable,
			DeployExitCodes.For( new DatabaseConnectionException( new InvalidOperationException( "no route" ) ) ) );

		Assert.Equal(
			DeployExitCodes.DriverCreationFailed,
			DeployExitCodes.For( new DriverCreationException( "pg", new InvalidOperationException( "bad dsn" ) ) ) );
	}

	/// <summary>Deploys the standard bundle under <paramref name="name"/> with the fake driver.</summary>
	private CommandAppResult Deploy( string name, params string[] switches )
		=> Run(
			["deploy", WriteBundle( name ), "--driver", "pg", "--pg-host", "h", "--pg-database", "d", ..switches] );

	private CommandAppResult Run( params string[] args )
	{
		// snapshots the seeded journal state as this run's canned query results, so a test that sets
		// _journal before calling Run gets the database it described
		_journal.Attach( _driver );

		TypeRegistrar registrar = new ();

		registrar.RegisterInstance( typeof(IDatabaseDriverRegistry), _drivers );

		BundleTypeRegistry bundleTypes = new ();
		bundleTypes.RegisterBundleTypes();
		registrar.RegisterInstance( typeof(IBundleTypeRegistry), bundleTypes );

		BundleLayoutRegistry layouts = new ();
		layouts.RegisterLayouts();
		registrar.RegisterInstance( typeof(IBundleLayoutRegistry), layouts );
		registrar.RegisterInstance( typeof(BundlerIdentity), new BundlerIdentity( "sqetch-deploy", "0.1.0" ) );

		CommandAppTester tester = new ( registrar );

		tester.Configure( SqetchDeployApp.Configure );

		return tester.Run( args );
	}

	/// <summary>
	/// Writes a bundle of release 2026.07 with plans 'table' (step 'create') and 'index'
	/// (step 'add') to <paramref name="name"/> under the test root. The release starts the
	/// lineage unless <paramref name="dependency"/> names the release it follows.
	/// </summary>
	private string WriteBundle(
		string name, string type = ZipBundleType.TypeName, bool withRollbackScripts = true,
		string dependency = "" )
	{
		Bundle bundle = new (
			new ProjectDefinition( "demo", "", "postgres" ),
			BundleSelection.Finalized,
			Manifests.Journaling(),
			[new BundleRelease( "2026.07", "", dependency, true )],
			[
				new BundlePlan(
					"table", "", "2026.07", [],
					[
						new BundleStep(
							"create", "", [],
							() => new StepScripts(
								Script( "create table t ()" ), Script( "-- v" ),
								withRollbackScripts ? Script( "-- r" ) : null ) )
					] ),
				new BundlePlan(
					"index", "", "2026.07", ["table"],
					[
						new BundleStep(
							"add", "", ["create"],
							() => new StepScripts(
								Script( "create index i" ), Script( "-- v" ),
								withRollbackScripts ? Script( "-- r" ) : null ) )
					] )
			] );

		string path = Path.Combine( _root, name );

		BundleTypeRegistry registry = new ();
		registry.RegisterBundleTypes();

		using( IBundleWriter writer = registry.Create( type ).Create( path ) )
			BundlePackager.Write( bundle, new DefaultBundleLayout(), writer, "sqetch-deploy", "0.1.0", Bundled );

		return path;
	}

	private static MemoryStream Script( string content ) => new ( Encoding.UTF8.GetBytes( content ) );

	private sealed class FakeDriverFactory( FakeDatabaseDriver driver ) : IDatabaseDriverFactory
	{
		public string Name => "pg";

		public IDictionary<string, string>? ReceivedParameters { get; private set; }

		public TimeSpan? ReceivedTimeout { get; private set; }

		public Exception? FailWith { get; set; }

		public DatabaseDriverParametersDefinition GetParametersDefinition()
			=> new (
				[
					new DatabaseDriverParameterDefinition( "host", "Database host" ),
					new DatabaseDriverParameterDefinition( "database", "Database name" ),
					new DatabaseDriverParameterDefinition( "connection-string", "Full connection string" )
				],
				[["host", "database"], ["connection-string"]] );

		public IDatabaseDriver Create( IDictionary<string, string> parameters, TimeSpan stepScriptTimeout )
		{
			if( FailWith is not null )
				throw FailWith;

			ReceivedParameters = parameters;
			ReceivedTimeout = stepScriptTimeout;

			return driver;
		}
	}
}
