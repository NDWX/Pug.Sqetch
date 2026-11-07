using System.Text.Json;

using Pug.Sqetch.Cli.Commands.Authoring;
using Pug.Sqetch.Planning.Models;

using Spectre.Console.Cli.Testing;

namespace Pug.Sqetch.Tests.Planning.Cli;

/// <summary>
/// 'sqetch project check' end to end: exit 0 on a sound project, 2 with one row per finding, 1 when it
/// cannot run; the rule checks skipped while the index cannot be trusted; '--fix' rebuilding the index
/// and nothing else; no user identity needed.
/// </summary>
[Collection( "cli" )]
public sealed class ProjectCheckCliTests : IDisposable
{
	private readonly string _root;
	private readonly string _originalDirectory;

	public ProjectCheckCliTests()
	{
		_root = Path.Combine( Path.GetTempPath(), "sqetch-cli-tests", Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( _root );

		_originalDirectory = Directory.GetCurrentDirectory();
		Directory.SetCurrentDirectory( _root );

		Succeed( "project", "user", "tester", "tester@example.com" );
		Succeed( "project", "init", "demo", "--engine", "sqlite" );
		Succeed( "journaling", "init", "sqlite" );
		Succeed( "plan", "create", "--name", "orders" );
		Succeed( "plan", "add-step", "--plan", "orders", "--name", "s1" );
	}

	public void Dispose()
	{
		Directory.SetCurrentDirectory( _originalDirectory );

		try
		{
			Directory.Delete( _root, recursive: true );
		}
		catch( IOException )
		{
		}
	}

	private static CommandAppResult Run( params string[] args ) => SqetchTester.Create().Run( args );

	private static void Succeed( params string[] args )
	{
		CommandAppResult result = Run( args );

		Assert.True( result.ExitCode == 0, result.Output );
	}

	private sealed record Row( string Check, string Release, string Plan, string Step, string Problem, bool FixedByReindex );

	/// <summary>Runs the check as JSON, returning its exit code and rows.</summary>
	private static ( int ExitCode, List<Row> Rows ) Check( params string[] extra )
	{
		CommandAppResult result = Run( [ "project", "check", "-o", "json", .. extra ] );

		List<Row> rows = JsonSerializer.Deserialize<JsonElement[]>( result.Output )!
			.Select( x => new Row(
					x.GetProperty( "check" ).GetString()!,
					x.GetProperty( "release" ).GetString()!,
					x.GetProperty( "plan" ).GetString()!,
					x.GetProperty( "step" ).GetString()!,
					x.GetProperty( "problem" ).GetString()!,
					x.GetProperty( "fixedByReindex" ).GetBoolean()
				)
			)
			.ToList();

		return ( result.ExitCode, rows );
	}

	[Fact]
	public void ASoundProjectPassesWithNoRows()
	{
		( int exitCode, List<Row> rows ) = Check();

		Assert.Equal( 0, exitCode );
		Assert.Empty( rows );
	}

	[Fact]
	public void AFindingExitsTwoWithARowLocatingIt()
	{
		File.Delete( Path.Combine( _root, "plans", "orders", "steps", "s1", "verify.sql" ) );

		( int exitCode, List<Row> rows ) = Check();

		Assert.Equal( ProjectCheckCommand.FindingsExitCode, exitCode );

		Row row = Assert.Single( rows );

		Assert.Equal( ProjectChecks.StepScriptsMissing, row.Check );
		Assert.Equal( "orders", row.Plan );
		Assert.Equal( "s1", row.Step );
		Assert.Contains( "verify", row.Problem );
		Assert.False( row.FixedByReindex );
	}

	[Fact]
	public void UnsetJournalingIsReportedSlotBySlot()
	{
		File.Delete( Path.Combine( _root, "journaling", JournalingSlots.FileName( JournalingSlot.GetLatestRelease ) ) );

		( int exitCode, List<Row> rows ) = Check();

		Assert.Equal( 2, exitCode );

		Row row = Assert.Single( rows );

		Assert.Equal( ProjectChecks.JournalingUnset, row.Check );
		Assert.Contains( nameof(JournalingSlot.GetLatestRelease), row.Problem );
	}

	/// <summary>
	/// With the index gone the rule checks would read through nothing, so they are skipped — the
	/// missing script is not reported — until '--fix' rebuilds it, after which they run and find it.
	/// </summary>
	[Fact]
	public void AStaleIndexSkipsTheRuleChecksUntilFixRebuildsIt()
	{
		File.Delete( Path.Combine( _root, "plans", "orders", "steps", "s1", "verify.sql" ) );
		File.Delete( Path.Combine( _root, "plan-index" ) );

		( int exitCode, List<Row> rows ) = Check();

		Assert.Equal( 2, exitCode );
		Assert.Contains( rows, row => row.Check == ProjectChecks.IndexMissing && row.FixedByReindex );
		Assert.Equal( ProjectChecks.ChecksSkipped, rows[^1].Check );
		Assert.DoesNotContain( rows, row => row.Check == ProjectChecks.StepScriptsMissing );
		Assert.False( File.Exists( Path.Combine( _root, "plan-index" ) ) );

		( exitCode, rows ) = Check( "--fix" );

		Assert.Equal( 2, exitCode );
		Assert.True( File.Exists( Path.Combine( _root, "plan-index" ) ) );
		Assert.Equal( ProjectChecks.StepScriptsMissing, Assert.Single( rows ).Check );
	}

	[Fact]
	public void FixRepairsAStepFolderLeftByAnInterruptedRename()
	{
		Directory.Move(
			Path.Combine( _root, "plans", "orders", "steps", "s1" ),
			Path.Combine( _root, "plans", "orders", "steps", "s2" )
		);

		( int exitCode, List<Row> rows ) = Check();

		Assert.Equal( 2, exitCode );
		Assert.Contains( rows, row => row.Check == ProjectChecks.StepNameMismatch && row.FixedByReindex );

		( exitCode, rows ) = Check( "--fix" );

		Assert.Equal( 0, exitCode );
		Assert.Empty( rows );
	}

	/// <summary>
	/// The rules read a plan's release from its plan.json: while that disagrees with the folder they
	/// would report a released plan's dependency as unreleased, so they wait for '--fix'.
	/// </summary>
	[Fact]
	public void AStalePlanReleaseBlocksTheRuleChecksAndFixUnblocksThem()
	{
		Succeed( "plan", "create", "--name", "lines", "--require-plans", "orders" );
		Succeed( "plan", "add-step", "--plan", "lines", "--name", "s1" );
		Succeed( "release", "create", "--name", "1.0", "--plans", "orders,lines" );

		string planJson = Path.Combine( _root, "releases", "1.0", "plans", "orders", "plan.json" );

		File.WriteAllText( planJson, File.ReadAllText( planJson ).Replace( "\"1.0\"", "\"\"" ) );

		( int exitCode, List<Row> rows ) = Check();

		Assert.Equal( 2, exitCode );
		Assert.Contains( rows, row => row.Check == ProjectChecks.PlanReleaseMismatch && row.Plan == "orders" );
		Assert.Equal( ProjectChecks.ChecksSkipped, rows[^1].Check );
		Assert.Contains( "--fix", rows[^1].Problem );
		Assert.DoesNotContain( rows, row => row.Check == ProjectChecks.ReleasedPlanMissingDependency );

		( exitCode, rows ) = Check( "--fix" );

		Assert.Equal( 0, exitCode );
		Assert.Empty( rows );
	}

	/// <summary>
	/// Reindexing over a conflicted document would fail partway, so '--fix' leaves the project alone
	/// and says so, rather than failing the command.
	/// </summary>
	[Fact]
	public void FixIsNotAppliedWhileADocumentCannotBeRead()
	{
		File.WriteAllText( Path.Combine( _root, "plans", "orders", "steps", "s1", "step.json" ), "<<<<<<< HEAD\n" );
		File.Delete( Path.Combine( _root, "plan-index" ) );

		( int exitCode, List<Row> rows ) = Check( "--fix" );

		Assert.Equal( 2, exitCode );
		Assert.Contains( rows, row => row.Check == ProjectChecks.DocumentUnreadable && row.Step == "s1" );
		Assert.Contains( "--fix was not applied", rows[^1].Problem );
		Assert.False( File.Exists( Path.Combine( _root, "plan-index" ) ) );
	}

	[Fact]
	public void ItNeedsNoUserIdentity()
	{
		File.Delete( Path.Combine( _root, ".sqetch-user" ) );

		Assert.Equal( 0, Check().ExitCode );
	}

	[Fact]
	public void OutsideAProjectItFailsWithOne()
	{
		string elsewhere = Path.Combine( _root, "elsewhere" );

		Directory.CreateDirectory( elsewhere );
		Directory.SetCurrentDirectory( elsewhere );

		Assert.Equal( 1, Run( "project", "check" ).ExitCode );
	}
}
