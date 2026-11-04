using Pug.Sqetch.Planning.Models;

using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;

namespace Pug.Sqetch.Tests.Planning.Cli;

/// <summary>
/// 'sqetch journaling init' and 'journaling templates', and the warning 'project init' gives for an
/// engine that names no driver. Each test gets its own project folder, made a git work-tree root so
/// the repository-level lookup stops there, and its own user-level template root.
/// </summary>
[Collection( "cli" )]
public sealed class JournalingTemplateCliTests : IDisposable
{
	private readonly string _temp;
	private readonly string _root;
	private readonly string _userTemplates;
	private readonly string _originalDirectory;
	private readonly TestConsole _warnings = new TestConsole().Width( 400 );

	public JournalingTemplateCliTests()
	{
		_temp = Path.Combine( Path.GetTempPath(), "sqetch-template-cli-tests", Guid.NewGuid().ToString( "N" ) );
		_root = Path.Combine( _temp, "project" );
		_userTemplates = Path.Combine( _temp, "user" );

		Directory.CreateDirectory( Path.Combine( _root, ".git" ) );

		_originalDirectory = Directory.GetCurrentDirectory();
		Directory.SetCurrentDirectory( _root );
	}

	public void Dispose()
	{
		Directory.SetCurrentDirectory( _originalDirectory );

		try
		{
			Directory.Delete( _temp, recursive: true );
		}
		catch( IOException )
		{
		}
	}

	private CommandAppResult Run( params string[] args )
		=> SqetchTester.Create( _userTemplates, warnings: _warnings ).Run( args );

	private void InitializeProject( params string[] options )
	{
		Assert.Equal( 0, Run( "project", "user", "tester", "tester@example.com" ).ExitCode );
		Assert.Equal( 0, Run( [ "project", "init", "demo", ..options ] ).ExitCode );
	}

	private static void WriteTemplate( string root, string driver, string name, string marker, bool optional = true )
	{
		string folder = Path.Combine( root, "database-drivers", driver, "journaling-templates", name );

		Directory.CreateDirectory( folder );

		foreach( JournalingSlot slot in optional ? JournalingSlots.All : JournalingSlots.Required )
			File.WriteAllText( Path.Combine( folder, JournalingSlots.FileName( slot ) ), $"-- {marker} {slot}" );
	}

	private string Print( JournalingSlot slot ) => Run( "journaling", "print", slot.ToString() ).Output;

	[Fact]
	public void InitSetsEverySlotTheEnginesBuiltInTemplateHasAndLeavesSqlitesLockUnsetWithoutWarning()
	{
		InitializeProject( "--engine", "SQLite" );

		CommandAppResult result = Run( "journaling", "init" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Contains( "Driver\tsqlite", result.Output );
		Assert.Contains( "Level\tbuilt-in", result.Output );

		// SQLite has no session locks, so its template leaves the lock pair unset
		Assert.Contains( "Slots set\t16", result.Output );

		string[] lines = Run( "journaling", "list" ).Output.Split( '\n' );

		Assert.Contains( "AcquireLock\tno\tyes", lines );
		Assert.Contains( "ReleaseLock\tno\tyes", lines );
		Assert.Equal( 16, lines.Count( line => line.Split( '\t' )[1] == "yes" ) );
		Assert.Contains( "sqetch_journal", Print( JournalingSlot.JournalExists ) );
		Assert.Contains( "create table if not exists sqetch_journal", Print( JournalingSlot.PrepareJournal ) );

		// the built-in SQLite template leaves the lock out by design: SQLite has no session lock
		Assert.Equal( "", _warnings.Output );
	}

	[Fact]
	public void InitFromThePostgresTemplateSetsEverySlotIncludingTheLockPair()
	{
		InitializeProject( "--engine", "postgres" );

		CommandAppResult result = Run( "journaling", "init", "postgres" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Contains( "Slots set\t18", result.Output );
		Assert.All( Run( "journaling", "list" ).Output.Split( '\n' ), line => Assert.Equal( "yes", line.Split( '\t' )[1] ) );
		Assert.Contains( "pg_try_advisory_lock", Print( JournalingSlot.AcquireLock ) );
		Assert.Contains( "pg_advisory_unlock", Print( JournalingSlot.ReleaseLock ) );
		Assert.Equal( "", _warnings.Output );
	}

	[Fact]
	public void ATemplateWithoutTheOptionalSlotIsAcceptedAndSetsTheRest()
	{
		InitializeProject( "--engine", "sqlite" );
		WriteTemplate( Path.Combine( _root, ".config", "sqetch" ), "sqlite", "default", "team", optional: false );

		CommandAppResult result = Run( "journaling", "init" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Contains( "Level\trepository", result.Output );
		Assert.Contains( "Slots set\t15", result.Output );

		string[] lines = Run( "journaling", "list" ).Output.Split( '\n' );

		Assert.Contains( "JournalExists\tno\tyes", lines );
		Assert.Contains( "AcquireLock\tno\tyes", lines );
		Assert.Equal( 15, lines.Count( line => line.Contains( "\tyes\tno" ) ) );
		Assert.Equal( "-- team GetDeployedPlans", Print( JournalingSlot.GetDeployedPlans ) );

		// said now, rather than discovered later as status's exit 25
		Assert.Contains( "does not set the optional JournalExists, AcquireLock, ReleaseLock, so they stay unset", _warnings.Output );
		Assert.Contains( "'sqetch-deploy status' refuses a bundle without JournalExists", _warnings.Output );
		Assert.Contains( "nothing stops two deployments to one database running at once", _warnings.Output );
	}

	[Fact]
	public void ForcingATemplateWithoutTheOptionalSlotKeepsTheOldOneAndWarns()
	{
		InitializeProject( "--engine", "sqlite" );
		Assert.Equal( 0, Run( "journaling", "init" ).ExitCode );
		WriteTemplate( Path.Combine( _root, ".config", "sqetch" ), "sqlite", "default", "team", optional: false );

		CommandAppResult result = Run( "journaling", "init", "--force" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Contains( "Slots set\t15", result.Output );
		Assert.Contains( "the template does not set JournalExists", _warnings.Output );

		// the built-in template the project started from set no lock either, so it is unset, not kept
		Assert.Contains( "does not set the optional AcquireLock, ReleaseLock, so they stay unset", _warnings.Output );
		Assert.Contains( "sqlite_master", Print( JournalingSlot.JournalExists ) );
		Assert.Equal( "-- team GetDeployedPlans", Print( JournalingSlot.GetDeployedPlans ) );
	}

	[Fact]
	public void AUserTemplateWithoutTheOptionalSlotIsAccepted()
	{
		InitializeProject( "--engine", "sqlite" );
		WriteTemplate( _userTemplates, "sqlite", "audit", "mine", optional: false );

		Assert.Equal( 0, Run( "journaling", "init", "--template", "audit" ).ExitCode );
		Assert.Equal( "-- mine RolledBackStep", Print( JournalingSlot.RolledBackStep ) );
	}

	[Fact]
	public void ATemplateMissingARequiredSlotIsStillRefused()
	{
		InitializeProject( "--engine", "sqlite" );
		WriteTemplate( _userTemplates, "sqlite", "audit", "mine", optional: false );
		File.Delete( Path.Combine(
			_userTemplates, "database-drivers", "sqlite", "journaling-templates", "audit",
			JournalingSlots.FileName( JournalingSlot.PrepareJournal ) ) );

		CommandAppResult result = Run( "journaling", "init", "--template", "audit" );

		Assert.Equal( 1, result.ExitCode );
		Assert.Contains( "missing: PrepareJournal.sql", result.Output );
		Assert.DoesNotContain( "JournalExists.sql", result.Output );
	}

	[Fact]
	public void InitRefusesWhenOnlyTheOptionalSlotIsSetWithoutForce()
	{
		InitializeProject( "--engine", "sqlite" );
		Assert.Equal( 0, Run( "journaling", "set", "JournalExists", "select 1" ).ExitCode );

		CommandAppResult refused = Run( "journaling", "init" );

		Assert.Equal( 1, refused.ExitCode );
		Assert.Contains( "already set: JournalExists; pass --force", refused.Output );
		Assert.Equal( 0, Run( "journaling", "init", "--force" ).ExitCode );
	}

	[Fact]
	public void InitNeedsADriverWhenTheEngineNamesNone()
	{
		InitializeProject();

		CommandAppResult noEngine = Run( "journaling", "init" );

		Assert.Equal( 1, noEngine.ExitCode );
		Assert.Contains( "names no engine; pass a driver: mysql, postgres, sqlite, sqlserver", noEngine.Output );
	}

	[Fact]
	public void AFreeTextEngineIsAcceptedWithAWarningButInitNeedsTheDriver()
	{
		InitializeProject( "--engine", "PostgreSQL 16" );

		Assert.Contains( "engine 'PostgreSQL 16' names no database driver", _warnings.Output );

		CommandAppResult result = Run( "journaling", "init" );

		Assert.Equal( 1, result.ExitCode );
		Assert.Contains( "engine 'PostgreSQL 16' names no database driver; pass one", result.Output );

		Assert.Equal( 0, Run( "journaling", "init", "postgres" ).ExitCode );
		Assert.Contains( "bigserial", Print( JournalingSlot.PrepareJournal ) );
	}

	[Fact]
	public void AnEngineNamingADriverGivesNoWarning()
	{
		InitializeProject( "--engine", "postgres" );

		Assert.Equal( "", _warnings.Output );
	}

	[Fact]
	public void ADriverOtherThanTheEngineIsUsedWithAWarning()
	{
		InitializeProject( "--engine", "sqlite" );

		Assert.Equal( 0, Run( "journaling", "init", "postgres" ).ExitCode );

		Assert.Contains( "engine is 'sqlite', but this template is for driver 'postgres'", _warnings.Output );
		Assert.Contains( "timestamptz", Print( JournalingSlot.PrepareJournal ) );
	}

	[Fact]
	public void InitRefusesToReplaceSetStatementsWithoutForce()
	{
		InitializeProject( "--engine", "sqlite" );
		Assert.Equal( 0, Run( "journaling", "set", "PrepareJournal", "create table mine( id int )" ).ExitCode );

		CommandAppResult refused = Run( "journaling", "init" );

		Assert.Equal( 1, refused.ExitCode );
		Assert.Contains( "already set: PrepareJournal; pass --force", refused.Output );
		Assert.Equal( "create table mine( id int )", Print( JournalingSlot.PrepareJournal ) );
		Assert.Contains( "GetLatestRelease\tno\tno", Run( "journaling", "list" ).Output );

		Assert.Equal( 0, Run( "journaling", "init", "--force" ).ExitCode );
		Assert.Contains( "sqetch_journal", Print( JournalingSlot.PrepareJournal ) );
	}

	[Fact]
	public void ARepositoryTemplateOverridesTheBuiltInOne()
	{
		InitializeProject( "--engine", "sqlite" );
		WriteTemplate( Path.Combine( _root, ".config", "sqetch" ), "sqlite", "default", "team" );

		CommandAppResult result = Run( "journaling", "init" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Contains( "Level\trepository", result.Output );
		Assert.Equal( "-- team GetDeployedPlans", Print( JournalingSlot.GetDeployedPlans ) );
	}

	[Fact]
	public void ANamedTemplateComesFromTheUserLevel()
	{
		InitializeProject( "--engine", "sqlite" );
		WriteTemplate( _userTemplates, "sqlite", "audit", "mine" );

		CommandAppResult result = Run( "journaling", "init", "--template", "audit" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Contains( "Level\tuser", result.Output );
		Assert.Equal( "-- mine RolledBackStep", Print( JournalingSlot.RolledBackStep ) );
	}

	[Fact]
	public void AnUnknownDriverOrTemplateIsRefused()
	{
		InitializeProject( "--engine", "sqlite" );

		CommandAppResult driver = Run( "journaling", "init", "oracle" );

		Assert.Equal( 1, driver.ExitCode );
		Assert.Contains( "Unknown database driver 'oracle'", driver.Output );

		CommandAppResult template = Run( "journaling", "init", "--template", "audit" );

		Assert.Equal( 1, template.ExitCode );
		Assert.Contains( "no journaling template 'audit' for driver 'sqlite'; available: default", template.Output );
		Assert.Contains( "PrepareJournal\tno\tno", Run( "journaling", "list" ).Output );
	}

	[Fact]
	public void TemplatesListsEveryLevelAndMarksTheShadowedOnes()
	{
		WriteTemplate( Path.Combine( _root, ".config", "sqetch" ), "sqlite", "default", "team" );
		WriteTemplate( _userTemplates, "sqlite", "audit", "mine" );
		WriteTemplate( _userTemplates, "oracle", "default", "mine" );

		// no project needed: templates are looked up from the current directory
		CommandAppResult result = Run( "journaling", "templates", "sqlite" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Equal(
			[
				$"sqlite\tdefault\trepository\t{Path.Combine( _root, ".config", "sqetch", "database-drivers", "sqlite", "journaling-templates", "default" )}\tyes",
				$"sqlite\taudit\tuser\t{Path.Combine( _userTemplates, "database-drivers", "sqlite", "journaling-templates", "audit" )}\tyes",
				"sqlite\tdefault\tbuilt-in\t(built in)\tshadowed"
			],
			result.Output.Split( '\n' ) );

		Assert.Contains( "oracle' names no database driver and is ignored", _warnings.Output );
	}
}
