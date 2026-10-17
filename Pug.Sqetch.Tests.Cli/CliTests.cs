using System.Text.Json;
using Pug.Sqetch.Cli;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;

namespace Pug.Sqetch.Tests.Cli;

/// <summary>
/// Drives the real 'sqetch' command tree through <see cref="CommandAppTester"/> against a
/// temp project directory. Commands resolve the project from the current directory, so the
/// fixture switches it per test; the "cli" collection keeps every class that does so on a
/// single sequential worker.
/// </summary>
[Collection( "cli" )]
public class CliTests : IDisposable
{
	private readonly string _root;
	private readonly string _originalDirectory;

	public CliTests()
	{
		_root = Path.Combine( Path.GetTempPath(), "sqetch-cli-tests", Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( _root );

		_originalDirectory = Directory.GetCurrentDirectory();
		Directory.SetCurrentDirectory( _root );
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

	private static CommandAppResult Run( params string[] args )
	{
		CommandAppTester tester = new ();

		tester.Configure( SqetchApp.Configure );

		return tester.Run( args );
	}

	private void InitializeProject()
	{
		Assert.Equal( 0, Run( "project", "user", "tester", "tester@example.com" ).ExitCode );
		Assert.Equal( 0, Run( "project", "init", "demo", "--shard-by-prefix" ).ExitCode );
	}

	[Fact]
	public void PlanCreatePrintsThePlanPath()
	{
		InitializeProject();

		CommandAppResult result = Run( "plan", "create", "--name", "customer-email", "--description", "Add column" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Equal( "plans/customer-email", result.Output );
		Assert.True( File.Exists( Path.Combine( _root, "plans", "customer-email", "plan.json" ) ) );
	}

	[Fact]
	public void AddStepPrintsStepAndScriptPathsAndSeedsScripts()
	{
		InitializeProject();
		Run( "plan", "create", "--name", "customer-email" );

		CommandAppResult result = Run( "plan", "add-step", "--plan", "customer-email", "--name", "add-column" );

		Assert.Equal( 0, result.ExitCode );

		string[] lines = result.Output.Split( '\n' );

		Assert.Equal( "plans/customer-email/steps/add-column", lines[0] );
		Assert.Contains( "plans/customer-email/steps/add-column/deploy.sql", lines );

		Assert.True( File.Exists( Path.Combine( _root, "plans", "customer-email", "steps", "add-column", "deploy.sql" ) ) );
	}

	[Fact]
	public void DefaultPlanCommandShowsPlanInfo()
	{
		InitializeProject();
		Run( "plan", "create", "--name", "customer-email", "--description", "Add column" );

		CommandAppResult result = Run( "plan", "--name", "customer-email" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Contains( "customer-email", result.Output );
		Assert.Contains( "(unreleased)", result.Output );
	}

	[Fact]
	public void ReleaseLifecycleAndPlanListFilters()
	{
		InitializeProject();

		Run( "plan", "create", "--name", "table" );
		Run( "plan", "create", "--name", "index", "--require-plans", "table" );
		Run( "plan", "create", "--name", "pending" );

		Assert.Equal( 0, Run( "release", "create", "--name", "2026.07", "--plans", "table,index" ).ExitCode );

		// unreleased by default
		Assert.Equal( "pending", Run( "plan", "list" ).Output.Split( '\t' )[0] );

		CommandAppResult released = Run( "plan", "list", "--released" );

		Assert.Equal(
			["table", "index"],
			released.Output.Split( '\n' ).Select( x => x.Split( '\t' )[0] ).ToArray() );

		Assert.Equal( 0, Run( "release", "finalize", "--name", "2026.07" ).ExitCode );

		// finalize-window filter implies released + finalized
		string today = DateTime.Now.ToString( "yyyy-MM-dd" );
		string tomorrow = DateTime.Now.AddDays( 1 ).ToString( "yyyy-MM-dd" );

		Assert.Contains( "table", Run( "plan", "list", "--finalized-after", today, "--finalized-before", tomorrow ).Output );
		Assert.Equal( "", Run( "plan", "list", "--released", "--finalized-after", tomorrow ).Output );
	}

	[Fact]
	public void SetReleaseMovesAndUnreleases()
	{
		InitializeProject();

		Run( "plan", "create", "--name", "movable" );
		Run( "release", "create", "--name", "2026.07" );
		Run( "release", "create", "--name", "2026.08", "--depends-on", "2026.07" );

		Assert.Equal( 0, Run( "plan", "set-release", "--plan", "movable", "--release", "2026.07" ).ExitCode );
		Assert.Contains( "2026.07", Run( "plan", "--name", "movable" ).Output );

		Assert.Equal( 0, Run( "plan", "set-release", "--plan", "movable", "--release", "2026.08" ).ExitCode );
		Assert.Contains( "2026.08", Run( "plan", "--name", "movable" ).Output );

		Assert.Equal( 0, Run( "plan", "set-release", "--plan", "movable", "--unreleased" ).ExitCode );
		Assert.Contains( "(unreleased)", Run( "plan", "--name", "movable" ).Output );
	}

	[Fact]
	public void OutputFormatsRenderJsonAndCsv()
	{
		InitializeProject();
		Run( "plan", "create", "--name", "solo", "--description", "one, with comma" );

		CommandAppResult json = Run( "plan", "list", "--output", "json" );

		Assert.Equal( 0, json.ExitCode );

		using JsonDocument document = JsonDocument.Parse( json.Output );

		Assert.Equal( "solo", document.RootElement[0].GetProperty( "name" ).GetString() );
		Assert.Equal( JsonValueKind.Array, document.RootElement[0].GetProperty( "dependencies" ).ValueKind );

		CommandAppResult csv = Run( "plan", "list", "--output", "csv" );

		string[] lines = csv.Output.Split( '\n' );

		Assert.StartsWith( "Name,Release,Created,Created By,Depends On,Description", lines[0] );
		Assert.Contains( "\"one, with comma\"", lines[1] );
	}

	[Fact]
	public void InteractiveConsoleGetsATableWithHeaders()
	{
		InitializeProject();
		Run( "plan", "create", "--name", "solo", "--description", "only one" );

		CommandAppTester tester = new ( console: new TestConsole().Width( 160 ).Interactive() );

		tester.Configure( SqetchApp.Configure );

		CommandAppResult result = tester.Run( "plan", "list" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Contains( "Name", result.Output );
		Assert.Contains( "Description", result.Output );
		Assert.Contains( "solo", result.Output );
	}

	[Fact]
	public void ReleaseListDefaultsToOpenReleases()
	{
		InitializeProject();

		Run( "plan", "create", "--name", "shipped" );
		Run( "release", "create", "--name", "2026.07", "--plans", "shipped" );
		Run( "release", "create", "--name", "2026.08", "--depends-on", "2026.07" );
		Run( "release", "finalize", "--name", "2026.07" );

		string[] openReleases = Run( "release", "list" ).Output
										.Split( '\n' ).Select( x => x.Split( '\t' )[0] ).ToArray();

		Assert.Equal( ["2026.08"], openReleases );
		Assert.Contains( "2026.07", Run( "release", "list", "--finalized" ).Output );
		Assert.Contains( "tester@example.com", Run( "release", "list", "--finalized", "--finalize-user", "tester@example.com" ).Output );
	}

	[Fact]
	public void DomainErrorsAreFriendlyAndFailTheCommand()
	{
		InitializeProject();

		Run( "plan", "create", "--name", "frozen" );
		Run( "release", "create", "--name", "2026.07", "--plans", "frozen" );
		Run( "release", "finalize", "--name", "2026.07" );

		CommandAppResult result = Run( "plan", "delete", "--name", "frozen" );

		Assert.Equal( 1, result.ExitCode );
		Assert.Contains( "finalized", result.Output );

		CommandAppResult unknown = Run( "plan", "--name", "no-such-plan" );

		Assert.Equal( 1, unknown.ExitCode );
		Assert.Contains( "does not exist", unknown.Output );
	}

	[Fact]
	public void NamesAreRestrictedToAlphanumericsAndSafePunctuation()
	{
		InitializeProject();

		// every allowed punctuation character is accepted
		Assert.Equal( 0, Run( "plan", "create", "--name", "plan-1_a+b(c)@d#e.f" ).ExitCode );

		foreach( string[] arguments in new[]
				{
					new[] { "plan", "create", "--name", "bad*plan" },
					new[] { "plan", "add-step", "--plan", "plan-1_a+b(c)@d#e.f", "--name", "bad/step" },
					new[] { "release", "create", "--name", "2026 07" }
				} )
		{
			CommandAppResult result = Run( arguments );

			Assert.NotEqual( 0, result.ExitCode );
			Assert.Contains( "may only contain", result.Output );
		}
	}

	[Fact]
	public void ReleaseDeleteRemovesTheReleaseAndFreesItsPlans()
	{
		InitializeProject();

		Run( "plan", "create", "--name", "undecided" );
		Run( "release", "create", "--name", "2026.09", "--plans", "undecided" );

		Assert.Equal( 0, Run( "release", "delete", "--name", "2026.09" ).ExitCode );

		Assert.Contains( "(unreleased)", Run( "plan", "--name", "undecided" ).Output );
		Assert.Equal( "", Run( "release", "list" ).Output );
		// unfinalized releases live unsharded directly under 'releases'
		Assert.False( Directory.Exists( Path.Combine( _root, "releases", "2026.09" ) ) );
	}

	/// <summary>'bundle' moved to 'sqetch-deploy'; it is deliberately not left behind as a stub.</summary>
	[Fact]
	public void BundleIsNoLongerACommand()
	{
		InitializeProject();

		CommandAppResult result = Run( "bundle" );

		Assert.NotEqual( 0, result.ExitCode );
	}
}
