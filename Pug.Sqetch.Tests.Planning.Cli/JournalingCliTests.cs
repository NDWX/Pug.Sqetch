using System.Text.Json;
using Pug.Sqetch.Planning.Cli;
using Spectre.Console.Cli.Testing;

namespace Pug.Sqetch.Tests.Planning.Cli;

/// <summary>
/// Drives 'sqetch journaling' through <see cref="CommandAppTester"/>. The "cli" collection
/// keeps every class that switches the process current directory on a single sequential
/// worker.
/// </summary>
[Collection( "cli" )]
public class JournalingCliTests : IDisposable
{
	private readonly string _root;
	private readonly string _originalDirectory;

	public JournalingCliTests()
	{
		_root = Path.Combine( Path.GetTempPath(), "sqetch-journaling-cli-tests", Guid.NewGuid().ToString( "N" ) );
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
		Assert.Equal( 0, Run( "project", "init", "demo" ).ExitCode );
	}

	[Fact]
	public void ListShowsEverySlotUnsetThenSetAfterSetting()
	{
		InitializeProject();

		string[] before = Run( "journaling", "list" ).Output.Split( '\n' );

		Assert.Equal( 15, before.Length );
		Assert.All( before, line => Assert.EndsWith( "\tno", line ) );
		Assert.Contains( before, line => line.StartsWith( "PrepareJournal\t" ) );

		Assert.Equal(
			0, Run( "journaling", "set", "PrepareJournal", "create table journal( release text )" ).ExitCode );

		string[] after = Run( "journaling", "list" ).Output.Split( '\n' );

		Assert.Contains( "PrepareJournal\tyes", after );
		Assert.Contains( after, line => line.StartsWith( "DeployingRelease\t" ) && line.EndsWith( "\tno" ) );
	}

	[Fact]
	public void ListWithStatementsAddsTheSqlColumn()
	{
		InitializeProject();

		Run( "journaling", "set", "PrepareJournal", "create table journal( release text )" );

		string[] lines = Run( "journaling", "list", "--statements" ).Output.Split( '\n' );

		Assert.Contains( "PrepareJournal\tyes\tcreate table journal( release text )", lines );
		Assert.Contains( "GetLatestRelease\tno", lines );
	}

	[Fact]
	public void ListAsJsonReportsSlotAndSetFlag()
	{
		InitializeProject();

		Run( "journaling", "set", "PrepareJournal", "create table journal( release text )" );

		CommandAppResult result = Run( "journaling", "list", "--output", "json" );

		Assert.Equal( 0, result.ExitCode );

		using JsonDocument document = JsonDocument.Parse( result.Output );

		JsonElement prepare = document.RootElement.EnumerateArray()
			.Single( x => x.GetProperty( "slot" ).GetString() == "PrepareJournal" );

		Assert.True( prepare.GetProperty( "set" ).GetBoolean() );

		JsonElement query = document.RootElement.EnumerateArray()
			.Single( x => x.GetProperty( "slot" ).GetString() == "GetLatestRelease" );

		Assert.False( query.GetProperty( "set" ).GetBoolean() );
	}

	[Fact]
	public void SetFromArgumentAndPrintRoundTrip()
	{
		InitializeProject();

		Assert.Equal(
			0, Run( "journaling", "set", "ReleaseDeployed", "insert into journal( release ) values( @release )" ).ExitCode );

		CommandAppResult result = Run( "journaling", "print", "ReleaseDeployed" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Equal( "insert into journal( release ) values( @release )", result.Output.Trim() );
	}

	[Fact]
	public void SetFromFileAndPrintRoundTrip()
	{
		InitializeProject();

		string file = Path.Combine( _root, "prepare.sql" );
		File.WriteAllText( file, "create table journal(\n\trelease text not null\n)" );

		Assert.Equal( 0, Run( "journaling", "set", "PrepareJournal", "--file", file ).ExitCode );

		CommandAppResult result = Run( "journaling", "print", "PrepareJournal" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Equal( "create table journal(\n\trelease text not null\n)", result.Output.Trim() );
	}

	[Fact]
	public void SetFromStdinReadsToEndOfInput()
	{
		InitializeProject();

		TextReader original = Console.In;

		try
		{
			Console.SetIn( new StringReader( "create table journal( release text )" ) );

			Assert.Equal( 0, Run( "journaling", "set", "PrepareJournal", "--stdin" ).ExitCode );
		}
		finally
		{
			Console.SetIn( original );
		}

		Assert.Equal( "create table journal( release text )", Run( "journaling", "print", "PrepareJournal" ).Output.Trim() );
	}

	[Fact]
	public void SetRequiresExactlyOneInputSource()
	{
		InitializeProject();

		CommandAppResult none = Run( "journaling", "set", "PrepareJournal" );
		Assert.Equal( 1, none.ExitCode );
		Assert.Contains( "exactly one", none.Output );

		CommandAppResult both = Run( "journaling", "set", "PrepareJournal", "select 1", "--stdin" );
		Assert.Equal( 1, both.ExitCode );
		Assert.Contains( "exactly one", both.Output );
	}

	[Fact]
	public void BadSlotNameListsTheValidOnes()
	{
		InitializeProject();

		CommandAppResult result = Run( "journaling", "print", "NoSuchSlot" );

		Assert.Equal( 1, result.ExitCode );
		Assert.Contains( "not a journaling slot", result.Output );
		Assert.Contains( "PrepareJournal", result.Output );
	}

	[Fact]
	public void PrintingAnUnsetSlotIsAnError()
	{
		InitializeProject();

		CommandAppResult result = Run( "journaling", "print", "PrepareJournal" );

		Assert.Equal( 1, result.ExitCode );
		Assert.Contains( "is not set", result.Output );
	}

	// ------------------------------------------------------------------ parameters

	[Fact]
	public void ParametersListsEverySlotWithTheParametersItsStatementsMayUse()
	{
		InitializeProject();

		CommandAppResult result = Run( "journaling", "parameters" );

		Assert.Equal( 0, result.ExitCode );

		string[] lines = result.Output.Split( '\n', StringSplitOptions.RemoveEmptyEntries );

		Assert.Equal( 15, lines.Length );
		Assert.Contains( lines, line => line.StartsWith( "DeployingStep\t" ) );

		// shown with the '@' a maintainer types, narrowing to the unit the slot names
		Assert.Contains( "DeployingStep\t@project @release @plan @step @description", result.Output );
		Assert.Contains( "GetLatestRelease\t@project", result.Output );
	}

	[Fact]
	public void ParametersNarrowsToOneSlot()
	{
		InitializeProject();

		CommandAppResult result = Run( "journaling", "parameters", "getdeployedplans" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Equal( "GetDeployedPlans\t@project @release", result.Output.Trim() );
	}

	[Fact]
	public void ParametersRendersAsJson()
	{
		InitializeProject();

		CommandAppResult result = Run( "journaling", "parameters", "PlanDeployed", "-o", "json" );

		Assert.Equal( 0, result.ExitCode );

		using JsonDocument json = JsonDocument.Parse( result.Output );

		JsonElement row = Assert.Single( json.RootElement.EnumerateArray().ToArray() );

		Assert.Equal( "PlanDeployed", row.GetProperty( "slot" ).GetString() );
		Assert.Equal(
			["@project", "@release", "@plan", "@description", "@utcTimestamp"],
			row.GetProperty( "parameters" ).EnumerateArray().Select( x => x.GetString()! ).ToArray() );
	}

	/// <summary>
	/// The parameter contract does not depend on the project, so this is the one journaling command
	/// that answers outside one — a maintainer can consult it before 'project init'.
	/// </summary>
	[Fact]
	public void ParametersNeedsNoProject()
	{
		CommandAppResult result = Run( "journaling", "parameters", "PrepareJournal" );

		Assert.Equal( 0, result.ExitCode );
		Assert.Contains( "@project", result.Output );
	}

	[Fact]
	public void ParametersRejectsABadSlotName()
	{
		CommandAppResult result = Run( "journaling", "parameters", "NoSuchSlot" );

		Assert.Equal( 1, result.ExitCode );
		Assert.Contains( "not a journaling slot", result.Output );
	}
}
