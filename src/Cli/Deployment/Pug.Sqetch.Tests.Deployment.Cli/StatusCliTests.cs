using System.Text;
using System.Text.Json;

using Pug.Sqetch.Deployment.Bundling;
using Pug.Sqetch.Deployment.Bundling.BundleTypes;
using Pug.Sqetch.Deployment.Bundling.Layouts;
using Pug.Sqetch.Deployment.Cli.Commands;
using Pug.Sqetch.Deployment;
using Pug.Sqetch.Shared;
using Pug.Sqetch.Cli;
using Pug.Sqetch.Deployment.Cli;
using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Planning.Models;

using Spectre.Console.Cli.Testing;

namespace Pug.Sqetch.Tests.Deployment.Cli;

/// <summary>
/// Drives 'sqetch-deploy status' through the production configuration with a fake driver, as
/// <see cref="DeployCliTests"/> does for 'deploy'. The fake records every transaction, so each test
/// can also show that nothing was committed.
/// </summary>
public class StatusCliTests : IDisposable
{
	private static readonly DateTime Bundled = new( 2026, 10, 17, 3, 15, 42, DateTimeKind.Utc );

	private readonly string _root;
	private readonly FakeDatabaseDriver _driver = new();
	private readonly FakeJournal _journal = new();
	private readonly CountingFactory _factory;
	private readonly DatabaseDriverRegistry _drivers = new();

	public StatusCliTests()
	{
		_root = Path.Combine( Path.GetTempPath(), "sqetch-status-cli-tests", Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( _root );

		_factory = new CountingFactory( _driver );

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

	private void AssertNothingChanged()
	{
		Assert.All( _driver.Transactions, transaction => Assert.False( transaction.Committed ) );
		Assert.All( _driver.Transactions, transaction => Assert.True( transaction.RolledBack ) );
		Assert.Empty( _driver.AppliedScripts );
		Assert.DoesNotContain( _driver.Events, e => e.StartsWith( "script" ) || e.StartsWith( "journal-statement" ) );
		Assert.True( _driver.Disposed );
	}

	[Fact]
	public void AFreshDatabaseListsEveryPlanInDeployOrderAndExitsZero()
	{
		_journal.JournalExists = false;

		CommandAppResult result = Status( WriteBundle( "demo.zip" ) );

		Assert.Equal( 0, result.ExitCode );
		Assert.Equal(
			[ "database\tfresh", "releases\t2026.07\tno\t2", "plans\t2026.07\ttable", "plans\t2026.07\tindex" ],
			Lines( result ) );

		// only JournalExists ran: no journal to read
		Assert.Single( _driver.Transactions );
		Assert.Equal( [ "journal JournalExists" ], _driver.Transactions[0].JournalingCalls.Select( c => c.Statement ) );
		AssertNothingChanged();
	}

	[Fact]
	public void DriverParametersAreHandedToTheDriver()
	{
		_journal.JournalExists = false;

		Assert.Equal( 0, Status( WriteBundle( "demo.zip" ) ).ExitCode );
		Assert.Equal( "h", _factory.ReceivedParameters!["host"] );
		Assert.Equal( "d", _factory.ReceivedParameters["database"] );
	}

	[Fact]
	public void ExitCodeIs81OnlyWithTheSwitchWhenSomethingWouldDeploy()
	{
		_journal.JournalExists = false;

		string bundle = WriteBundle( "demo.zip" );

		Assert.Equal( 0, Status( bundle ).ExitCode );
		Assert.Equal( DeployExitCodes.DeploymentPending, Status( bundle, "--exit-code" ).ExitCode );
		Assert.Equal( 81, DeployExitCodes.DeploymentPending );
	}

	[Fact]
	public void ExitCodeStaysZeroWhenTheDatabaseIsUpToDate()
	{
		_journal.Latest = new JournaledRelease( "2026.07", Completed: true );

		CommandAppResult result = Status( WriteBundle( "demo.zip" ), "--exit-code" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Equal( [ "database\tcomplete\t2026.07" ], Lines( result ) );
		AssertNothingChanged();
	}

	[Fact]
	public void AnEmptyJournalIsReportedAsSuchAndDeploysEverything()
	{
		CommandAppResult result = Status( WriteBundle( "demo.zip" ) );

		Assert.Equal(
			[ "database\tempty", "releases\t2026.07\tno\t2", "plans\t2026.07\ttable", "plans\t2026.07\tindex" ],
			Lines( result ) );
		AssertNothingChanged();
	}

	[Fact]
	public void APartlyDeployedReleaseListsOnlyTheRemainingPlans()
	{
		_journal.Latest = new JournaledRelease( "2026.07", Completed: false );
		_journal.DeployedPlans["2026.07"] = [ "table" ];

		CommandAppResult result = Status( WriteBundle( "demo.zip" ), "--exit-code" );

		Assert.Equal( DeployExitCodes.DeploymentPending, result.ExitCode );
		Assert.Equal(
			[ "database\tpartial\t2026.07", "releases\t2026.07\tyes\t1", "plans\t2026.07\tindex" ],
			Lines( result ) );
		Assert.Equal(
			[ "journal JournalExists", "journal GetLatestRelease", "journal GetDeployedPlans" ],
			_driver.Transactions[0].JournalingCalls.Select( c => c.Statement ) );
		AssertNothingChanged();
	}

	[Fact]
	public void AReleaseWithOnlyItsCompletionLeftIsListedSoExitCode81IsExplained()
	{
		_journal.Latest = new JournaledRelease( "2026.07", Completed: false );
		_journal.DeployedPlans["2026.07"] = [ "table", "index" ];

		CommandAppResult result = Status( WriteBundle( "demo.zip" ), "--exit-code", "-o", "json" );

		Assert.Equal( DeployExitCodes.DeploymentPending, result.ExitCode );

		using JsonDocument document = JsonDocument.Parse( result.Output );
		JsonElement release = Assert.Single( document.RootElement.GetProperty( "releases" ).EnumerateArray() );

		Assert.Equal( "2026.07", release.GetProperty( "release" ).GetString() );
		Assert.True( release.GetProperty( "resumed" ).GetBoolean() );
		Assert.Equal( 0, release.GetProperty( "plans" ).GetInt32() );
		Assert.Empty( document.RootElement.GetProperty( "plans" ).EnumerateArray() );
		AssertNothingChanged();
	}

	[Fact]
	public void JsonCarriesTheDatabaseTheContinuationTheVerdictAndThePlans()
	{
		_journal.Latest = new JournaledRelease( "2026.06", Completed: true );

		CommandAppResult result = Status( WriteBundle( "continuation.zip", dependency: "2026.06" ), "-o", "json" );

		Assert.Equal( 0, result.ExitCode );

		using JsonDocument document = JsonDocument.Parse( result.Output );
		JsonElement root = document.RootElement;

		Assert.Equal( "complete", root.GetProperty( "database" ).GetProperty( "state" ).GetString() );
		Assert.Equal( "2026.06", root.GetProperty( "database" ).GetProperty( "release" ).GetString() );
		Assert.Equal( "2026.06", root.GetProperty( "continuingFrom" ).GetString() );
		Assert.False( root.GetProperty( "upToDate" ).GetBoolean() );
		Assert.Equal(
			[ ("2026.07", "table"), ("2026.07", "index") ],
			root.GetProperty( "plans" ).EnumerateArray()
				.Select( x => ( x.GetProperty( "release" ).GetString()!, x.GetProperty( "plan" ).GetString()! ) ) );
		AssertNothingChanged();
	}

	[Fact]
	public void JsonOfAFreshDatabaseHasNullReleaseAndNullContinuation()
	{
		_journal.JournalExists = false;

		using JsonDocument document = JsonDocument.Parse( Status( WriteBundle( "demo.zip" ), "-o", "json" ).Output );
		JsonElement root = document.RootElement;

		Assert.Equal( "fresh", root.GetProperty( "database" ).GetProperty( "state" ).GetString() );
		Assert.Equal( JsonValueKind.Null, root.GetProperty( "database" ).GetProperty( "release" ).ValueKind );
		Assert.Equal( JsonValueKind.Null, root.GetProperty( "continuingFrom" ).ValueKind );
	}

	[Fact]
	public void JsonOfAnUpToDateDatabaseHasNoPlans()
	{
		_journal.Latest = new JournaledRelease( "2026.07", Completed: true );

		using JsonDocument document = JsonDocument.Parse( Status( WriteBundle( "demo.zip" ), "-o", "json" ).Output );

		Assert.True( document.RootElement.GetProperty( "upToDate" ).GetBoolean() );
		Assert.Equal( 0, document.RootElement.GetProperty( "plans" ).GetArrayLength() );
	}

	[Fact]
	public void CsvWritesOneKeyedBlockPerSection()
	{
		_journal.JournalExists = false;

		CommandAppResult result = Status( WriteBundle( "demo.zip" ), "-o", "csv" );

		Assert.Equal( 0, result.ExitCode );

		string[] lines = result.Output.ReplaceLineEndings( "\n" ).Split( '\n', StringSplitOptions.RemoveEmptyEntries );

		Assert.Contains( "database", lines );
		Assert.Contains( "plans", lines );
		Assert.Contains( "State,Release", lines );
		Assert.Contains( "fresh,", lines );
		Assert.Contains( "Release,Plan", lines );
		Assert.Contains( "2026.07,table", lines );
		Assert.Contains( "2026.07,index", lines );
		Assert.True( Array.IndexOf( lines, "database" ) < Array.IndexOf( lines, "plans" ) );
	}

	[Fact]
	public void ABundleWithoutJournalExistsIsRefusedWithExit25AndNoDriverIsCreated()
	{
		string bundle = WriteBundle( "old.zip", journaling: FakeJournal.RequiredStatements() );

		CommandAppResult result = Status( bundle );

		Assert.Equal( DeployExitCodes.IncompleteJournaling, result.ExitCode );
		Assert.Equal( 25, result.ExitCode );
		Assert.Equal( 0, _factory.Created );
		Assert.Empty( _driver.Events );
		Assert.Contains( "JournalExists", result.Output );
	}

	[Fact]
	public void ABundleThatDoesNotFitTheDatabaseExits23AndChangesNothing()
	{
		_journal.Latest = new JournaledRelease( "2026.06", Completed: false );

		CommandAppResult result = Status( WriteBundle( "continuation.zip", dependency: "2026.06" ) );

		Assert.Equal( DeployExitCodes.IncompatibleBundle, result.ExitCode );
		Assert.Contains( "partially deployed", System.Text.RegularExpressions.Regex.Replace( result.Output, @"\s+", " " ) );
		AssertNothingChanged();
	}

	[Fact]
	public void ADriverThatCannotBeCreatedExits32AndNothingIsAsked()
	{
		_factory.FailWith = new InvalidOperationException( "no such host" );

		CommandAppResult result = Status( WriteBundle( "demo.zip" ) );

		Assert.Equal( DeployExitCodes.DriverCreationFailed, result.ExitCode );
		Assert.Empty( _driver.Transactions );
	}

	[Fact]
	public void AJournalQueryThatFailsExits42AndChangesNothing()
	{
		_driver.FailingJournalingQueries.Add( FakeJournal.Marker( JournalingSlot.GetLatestRelease ) );

		CommandAppResult result = Status( WriteBundle( "demo.zip" ) );

		Assert.Equal( DeployExitCodes.JournalQueryFailed, result.ExitCode );
		AssertNothingChanged();
	}

	[Fact]
	public void TheDriverOptionIsRequired()
	{
		CommandAppResult result = Run( "status", WriteBundle( "demo.zip" ) );

		Assert.Equal( DeployExitCodes.UsageError, result.ExitCode );
		Assert.Contains( "--driver is required", result.Output );
	}

	[Fact]
	public void AMissingBundleExits21()
	{
		CommandAppResult result = Status( Path.Combine( _root, "nope.zip" ) );

		Assert.Equal( DeployExitCodes.BundleNotFound, result.ExitCode );
		Assert.Equal( 0, _factory.Created );
	}

	// ------------------------------------------------------------------ helpers

	private static string[] Lines( CommandAppResult result )
		=> result.Output.ReplaceLineEndings( "\n" ).Split( '\n', StringSplitOptions.RemoveEmptyEntries );

	private CommandAppResult Status( string bundle, params string[] switches )
		=> Run( [ "status", bundle, "--driver", "pg", "--pg-host", "h", "--pg-database", "d", .. switches ] );

	private CommandAppResult Run( params string[] args )
	{
		_journal.Attach( _driver );

		TypeRegistrar registrar = new();

		registrar.RegisterInstance( typeof(IDatabaseDriverRegistry), _drivers );

		BundleTypeRegistry bundleTypes = new();
		bundleTypes.RegisterBundleTypes();
		registrar.RegisterInstance( typeof(IBundleTypeRegistry), bundleTypes );

		BundleLayoutRegistry layouts = new();
		layouts.RegisterLayouts();
		registrar.RegisterInstance( typeof(IBundleLayoutRegistry), layouts );
		registrar.RegisterInstance( typeof(BundlerIdentity), new BundlerIdentity( "sqetch-deploy", "0.1.0" ) );

		CommandAppTester tester = new( registrar );

		tester.Configure( SqetchDeployApp.Configure );

		return tester.Run( args );
	}

	/// <summary>
	/// A bundle of release 2026.07 with plans 'table' then 'index'; it follows <paramref name="dependency"/>
	/// when that names a release, making it a continuation bundle.
	/// </summary>
	private string WriteBundle( string name, string dependency = "", JournalingStatements? journaling = null )
	{
		Bundle bundle = new(
			new ProjectDefinition( "demo", "", "postgres" ),
			BundleSelection.Finalized,
			journaling ?? Manifests.Journaling(),
			[ new BundleRelease( "2026.07", "", dependency, true ) ],
			[
				new BundlePlan(
					"table",
					"",
					"2026.07",
					[ ],
					[
						new BundleStep(
							"create",
							"",
							[ ],
							() => new StepScripts( Script( "create table t ()" ), Script( "-- v" ), Script( "-- r" ) )
						)
					]
				),
				new BundlePlan(
					"index",
					"",
					"2026.07",
					[ "table" ],
					[
						new BundleStep(
							"add",
							"",
							[ "create" ],
							() => new StepScripts( Script( "create index i" ), Script( "-- v" ), Script( "-- r" ) )
						)
					]
				)
			]
		);

		string path = Path.Combine( _root, name );

		BundleTypeRegistry registry = new();
		registry.RegisterBundleTypes();

		using( IBundleWriter writer = registry.Create( ZipBundleType.TypeName ).Create( path ) )
			BundlePackager.Write( bundle, new DefaultBundleLayout(), writer, "sqetch-deploy", "0.1.0", Bundled );

		return path;
	}

	private static MemoryStream Script( string content ) => new( Encoding.UTF8.GetBytes( content ) );

	private sealed class CountingFactory( FakeDatabaseDriver driver ) : IDatabaseDriverFactory
	{
		public string Name => "pg";

		public int Created { get; private set; }

		public IDictionary<string, string>? ReceivedParameters { get; private set; }

		public Exception? FailWith { get; set; }

		public DatabaseDriverParametersDefinition GetParametersDefinition()
			=> new(
				[
					new DatabaseDriverParameterDefinition( "host", "Database host" ),
					new DatabaseDriverParameterDefinition( "database", "Database name" )
				],
				[ [ "host", "database" ] ]
			);

		public IDatabaseDriver Create( IDictionary<string, string> parameters, TimeSpan stepScriptTimeout )
		{
			Created++;

			if( FailWith is not null )
				throw FailWith;

			ReceivedParameters = parameters;

			return driver;
		}
	}
}
