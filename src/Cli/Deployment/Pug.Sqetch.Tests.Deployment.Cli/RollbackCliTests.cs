using System.Text;

using Pug.Sqetch.Cli;
using Pug.Sqetch.Deployment.Bundling;
using Pug.Sqetch.Deployment.Bundling.BundleTypes;
using Pug.Sqetch.Deployment.Bundling.Layouts;
using Pug.Sqetch.Deployment.Cli;
using Pug.Sqetch.Deployment.Cli.Commands;
using Pug.Sqetch.DatabaseDrivers.Sqlite;
using Pug.Sqetch.Planning.Models;

using Spectre.Console.Testing;
using Spectre.Console.Cli.Testing;

namespace Pug.Sqetch.Tests.Deployment.Cli;

/// <summary>
/// Drives 'sqetch-deploy rollback' through <see cref="CommandAppTester"/> hosting the production
/// composition against a real SQLite file, with the driver's own journaling template: what the
/// command undoes is read back out of the journal it wrote.
/// </summary>
public class RollbackCliTests : IDisposable
{
	private static readonly DateTime Bundled = new( 2026, 10, 17, 3, 15, 42, DateTimeKind.Utc );

	private readonly string _root;
	private readonly string _database;
	private TestConsole _warnings = new();

	public RollbackCliTests()
	{
		_root = Path.Combine( Path.GetTempPath(), "sqetch-rollback-cli-tests", Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( _root );

		_database = Path.Combine( _root, "db.sqlite" );
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

	private sealed record PlanSpec( string Name, string[]? Dependencies = null, string? Rollback = null );

	private sealed record ReleaseSpec( string Name, PlanSpec[] Plans, bool Finalized = true );

	public static TheoryData<string[]> BadSwitchSets
		=> new()
		{
			{ [ ] }, { [ "--all", "--to", "r1" ] }, { [ "--to", "r1", "--plans", "p1" ] }, { [ "--to", "" ] },
			{ [ "--plans", " , ; " ] }
		};

	[Theory]
	[MemberData( nameof(BadSwitchSets) )]
	public void BadSwitchesAreUsageErrors( string[] switches )
	{
		CommandAppResult result = Rollback( TwoReleases(), switches );

		Assert.Equal( DeployExitCodes.UsageError, result.ExitCode );
		Assert.False( File.Exists( _database ) );
	}

	[Fact]
	public void NamingNoneOfTheTargetsSaysSo()
	{
		CommandAppResult result = Rollback( TwoReleases() );

		Assert.Contains( "specify exactly one of --to <release>, --all or --plans <list>", Flat( result ) );
	}

	[Fact]
	public void RollsBackToAReleaseAndThenHasNothingLeftToDo()
	{
		string bundle = TwoReleases();

		Deploy( bundle );

		CommandAppResult first = Rollback( bundle, "--to", "r1" );

		Assert.Equal( 0, first.ExitCode );
		Assert.Contains( "rolling back release 'r2'", first.Output );
		Assert.Contains( "rollback complete", first.Output );
		Assert.Equal( [ "t_p1" ], Tables() );

		CommandAppResult second = Rollback( bundle, "--to", "r1" );

		Assert.Equal( 0, second.ExitCode );
		Assert.Contains( "nothing to roll back", second.Output );
		Assert.DoesNotContain( "rollback complete", second.Output );

		// what was undone deploys again
		Assert.Equal( 0, Deploy( bundle ).ExitCode );
		Assert.Equal( [ "t_p1", "t_p2" ], Tables() );
	}

	[Fact]
	public void RollingBackADatabaseNeverDeployedToFailsTheJournalQueryAndCreatesNothing()
	{
		CommandAppResult result = Rollback( TwoReleases(), "--to", "r1" );

		Assert.Equal( DeployExitCodes.JournalQueryFailed, result.ExitCode );
		Assert.Empty( AllTables() );
	}

	[Fact]
	public void RollsBackEverything()
	{
		string bundle = TwoReleases();

		Deploy( bundle );

		CommandAppResult result = Rollback( bundle, "--all" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Contains( "rollback complete", result.Output );
		Assert.Empty( Tables() );
	}

	private string ThreePlans()
		=> WriteBundle(
			"three.zip",
			new ReleaseSpec( "r1", [ new PlanSpec( "p1" ) ] ),
			new ReleaseSpec( "r2", [ new PlanSpec( "p2" ), new PlanSpec( "p3" ) ] )
		);

	[Fact]
	public void RollsBackOneOfTwoIndependentPlansOfTheLatestReleaseAndItDeploysAgain()
	{
		string bundle = ThreePlans();

		Deploy( bundle );

		CommandAppResult result = Rollback( bundle, "--plans", "p3" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Contains( "rollback complete", result.Output );
		Assert.Equal( [ "t_p1", "t_p2" ], Tables() );

		Assert.Equal( 0, Deploy( bundle ).ExitCode );
		Assert.Equal( [ "t_p1", "t_p2", "t_p3" ], Tables() );
	}

	[Fact]
	public void RollsBackEveryPlanOfTheLatestReleaseAndRerunningItHasNothingToDo()
	{
		string bundle = ThreePlans();

		Deploy( bundle );

		CommandAppResult first = Rollback( bundle, "--plans", "p3,p2" );

		Assert.Equal( 0, first.ExitCode );
		Assert.Contains( "rollback complete", first.Output );
		Assert.Equal( [ "t_p1" ], Tables() );

		CommandAppResult second = Rollback( bundle, "--plans", "p3,p2" );

		Assert.Equal( 0, second.ExitCode );
		Assert.Contains( "nothing to roll back", second.Output );
		Assert.DoesNotContain( "rollback complete", second.Output );
		Assert.Equal( [ "t_p1" ], Tables() );
	}

	[Fact]
	public void AnUnknownPlanIsRefusedAndNothingIsUndone()
	{
		string bundle = ThreePlans();

		Deploy( bundle );

		CommandAppResult result = Rollback( bundle, "--plans", "p3,nope" );

		Assert.Equal( DeployExitCodes.RollbackRefused, result.ExitCode );
		Assert.Contains( "nope", Flat( result ) );
		Assert.Equal( [ "t_p1", "t_p2", "t_p3" ], Tables() );
	}

	[Fact]
	public void AnUnknownReleaseIsRefused()
	{
		string bundle = TwoReleases();

		Deploy( bundle );

		CommandAppResult result = Rollback( bundle, "--to", "nope" );

		Assert.Equal( DeployExitCodes.RollbackRefused, result.ExitCode );
		Assert.Equal( [ "t_p1", "t_p2" ], Tables() );
	}

	[Fact]
	public void PlansWhoseDeployedDependantIsNotListedAreRefused()
	{
		string bundle = WriteBundle(
			"b.zip",
			new ReleaseSpec( "r1", [ new PlanSpec( "p1" ), new PlanSpec( "p2", [ "p1" ] ) ] )
		);

		Deploy( bundle );

		CommandAppResult result = Rollback( bundle, "--plans", "p1" );

		Assert.Equal( DeployExitCodes.RollbackRefused, result.ExitCode );
		Assert.Equal( [ "t_p1", "t_p2" ], Tables() );
	}

	[Fact]
	public void ABundleLackingADeployedReleaseIsIncompatible()
	{
		Deploy( TwoReleases() );

		// a continuation bundle: r1 is deployed but not in it
		string continuation = WriteBundle(
			"continuation.zip",
			"r1",
			new ReleaseSpec( "r2", [ new PlanSpec( "p2" ) ] )
		);

		CommandAppResult result = Rollback( continuation, "--all" );

		Assert.Equal( DeployExitCodes.IncompatibleBundle, result.ExitCode );
		Assert.Equal( [ "t_p1", "t_p2" ], Tables() );
	}

	[Fact]
	public void AFailingRollbackScriptIsItsOwnFailure()
	{
		string bundle = WriteBundle(
			"b.zip",
			new ReleaseSpec( "r1", [ new PlanSpec( "p1", Rollback: "select * from no_such_table" ) ] )
		);

		Deploy( bundle );

		CommandAppResult result = Rollback( bundle, "--all" );

		Assert.Equal( DeployExitCodes.RollbackFailed, result.ExitCode );
		Assert.Equal( [ "t_p1" ], Tables() );
	}

	[Fact]
	public void WarnsOncePerUnfinalizedReleaseThatItMayHaveDifferentRollbackScripts()
	{
		string bundle = WriteBundle(
			"b.zip",
			BundleSelection.Test,
			"",
			new ReleaseSpec( "r1", [ new PlanSpec( "p1" ), new PlanSpec( "p2" ) ], Finalized: false ),
			new ReleaseSpec( "r2", [ new PlanSpec( "p3" ) ], Finalized: false )
		);

		Deploy( bundle );

		CommandAppResult result = Rollback( bundle, "--all" );

		Assert.Equal( 0, result.ExitCode );

		string warnings = Flat( _warnings.Output );

		// r1 has two plans yet is named once; each release gets its own
		Assert.Equal( 1, warnings.Split( "release 'r1' is not finalized in this bundle" ).Length - 1 );
		Assert.Equal( 1, warnings.Split( "release 'r2' is not finalized in this bundle" ).Length - 1 );
		Assert.Contains( "may differ from what was deployed", warnings );
		Assert.Equal( 2, warnings.Split( "not finalized" ).Length - 1 );
		Assert.DoesNotContain( "not finalized", result.Output );
	}

	[Fact]
	public void DoesNotWarnForFinalizedReleases()
	{
		string bundle = TwoReleases();

		Deploy( bundle );

		Assert.Equal( 0, Rollback( bundle, "--all" ).ExitCode );
		Assert.Equal( "", _warnings.Output.Trim() );
	}

	private string TwoReleases()
		=> WriteBundle(
			"two.zip",
			new ReleaseSpec( "r1", [ new PlanSpec( "p1" ) ] ),
			new ReleaseSpec( "r2", [ new PlanSpec( "p2" ) ] )
		);

	private CommandAppResult Deploy( string bundle )
		=> Run( "deploy", bundle, "--driver", "sqlite", "--sqlite-file", _database );

	private CommandAppResult Rollback( string bundle, params string[] switches )
		=> Run( [ "rollback", bundle, "--driver", "sqlite", "--sqlite-file", _database, .. switches ] );

	private CommandAppResult Run( params string[] args )
	{
		_warnings = new TestConsole();

		TypeRegistrar registrar = SqetchDeployApp.CreateDefaultRegistrar();

		registrar.RegisterInstance( typeof(WarningWriter), new WarningWriter( _warnings ) );

		CommandAppTester tester = new( registrar );

		tester.Configure( SqetchDeployApp.Configure );

		return tester.Run( args );
	}

	/// <summary>The user tables the deployments created, leaving out the journal.</summary>
	private string[] Tables()
	{
		using Microsoft.Data.Sqlite.SqliteConnection connection = new( $"Data Source={_database}" );

		connection.Open();

		using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();

		command.CommandText =
			"select name from sqlite_master where type = 'table' and name like 't\\_%' escape '\\' order by name";

		using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();

		List<string> names = [ ];

		while( reader.Read() )
			names.Add( reader.GetString( 0 ) );

		return [ ..names ];
	}

	/// <summary>Every table in the database, the journal included.</summary>
	private string[] AllTables()
	{
		using Microsoft.Data.Sqlite.SqliteConnection connection = new( $"Data Source={_database}" );

		connection.Open();

		using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();

		command.CommandText = "select name from sqlite_master where type = 'table' order by name";

		using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();

		List<string> names = [ ];

		while( reader.Read() )
			names.Add( reader.GetString( 0 ) );

		return [ ..names ];
	}

	private static string Flat( CommandAppResult result ) => Flat( result.Output );

	private static string Flat( string text ) => System.Text.RegularExpressions.Regex.Replace( text, @"\s+", " " );

	private string WriteBundle( string name, params ReleaseSpec[] releases )
		=> WriteBundle( name, BundleSelection.Finalized, "", releases );

	private string WriteBundle( string name, string firstDependency, params ReleaseSpec[] releases )
		=> WriteBundle( name, BundleSelection.Finalized, firstDependency, releases );

	/// <summary>
	/// Each plan has one step creating table <c>t_&lt;plan&gt;</c>, with a rollback script dropping
	/// it unless the spec gives another. Releases follow one another from <paramref name="firstDependency"/>.
	/// </summary>
	private string WriteBundle(
		string name,
		BundleSelection selection,
		string firstDependency,
		params ReleaseSpec[] releases
	)
	{
		IReadOnlyDictionary<string, string> template = new SqliteDatabaseDriverFactory().GetJournalingTemplate();

		JournalingStatements journaling = new(
			JournalingSlots.All.Where( slot => template.ContainsKey( slot.ToString() ) )
				.ToDictionary( slot => slot, slot => (string?)template[slot.ToString()] )
		);

		List<BundleRelease> bundleReleases = [ ];
		List<BundlePlan> plans = [ ];
		string previous = firstDependency;

		foreach( ReleaseSpec release in releases )
		{
			bundleReleases.Add( new BundleRelease( release.Name, "", previous, release.Finalized ) );

			previous = release.Name;

			foreach( PlanSpec plan in release.Plans )
			{
				string table = $"t_{plan.Name}";

				plans.Add(
					new BundlePlan(
						plan.Name,
						"",
						release.Name,
						plan.Dependencies ?? [ ],
						[
							new BundleStep(
								"create",
								"",
								[ ],
								() => new StepScripts(
									Script( $"create table {table} ( id integer )" ),
									Script( $"select count(*) from {table}" ),
									Script( plan.Rollback ?? $"drop table if exists {table}" )
								)
							)
						]
					)
				);
			}
		}

		Bundle bundle = new(
			new ProjectDefinition( "demo", "", "sqlite" ),
			selection,
			journaling,
			bundleReleases,
			plans
		);

		string path = Path.Combine( _root, name );

		BundleTypeRegistry registry = new();
		registry.RegisterBundleTypes();

		using( IBundleWriter writer = registry.Create( ZipBundleType.TypeName ).Create( path ) )
			BundlePackager.Write( bundle, new DefaultBundleLayout(), writer, "sqetch-deploy", "0.1.0", Bundled );

		return path;
	}

	private static MemoryStream Script( string content ) => new( Encoding.UTF8.GetBytes( content ) );
}
