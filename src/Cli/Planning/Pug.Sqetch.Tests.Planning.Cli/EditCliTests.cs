using System.Text.Json;

using Spectre.Console.Cli.Testing;

namespace Pug.Sqetch.Tests.Planning.Cli;

/// <summary>
/// The edit commands end to end: 'plan update', 'release update', 'plan update-step',
/// 'plan rename-step' and 'plan move-step', on plan <c>orders</c> whose steps a ← b ← c form a
/// chain, and plan <c>base</c>, on which nothing depends yet.
/// </summary>
[Collection( "cli" )]
public sealed class EditCliTests : IDisposable
{
	private readonly string _root;
	private readonly string _originalDirectory;

	public EditCliTests()
	{
		_root = Path.Combine( Path.GetTempPath(), "sqetch-cli-tests", Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( _root );

		_originalDirectory = Directory.GetCurrentDirectory();
		Directory.SetCurrentDirectory( _root );

		Succeed( "project", "user", "tester", "tester@example.com" );
		Succeed( "project", "init", "demo" );
		Succeed( "plan", "create", "--name", "base" );
		Succeed( "plan", "create", "--name", "orders" );
		Succeed( "plan", "add-step", "--plan", "orders", "--name", "a" );
		Succeed( "plan", "add-step", "--plan", "orders", "--name", "b", "--requires", "a" );
		Succeed( "plan", "add-step", "--plan", "orders", "--name", "c", "--requires", "b" );
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

	private static string Succeed( params string[] args )
	{
		CommandAppResult result = Run( args );

		Assert.True( result.ExitCode == 0, result.Output );

		return result.Output;
	}

	private static void Refused( string message, params string[] args )
	{
		CommandAppResult result = Run( args );

		Assert.Equal( 1, result.ExitCode );
		Assert.Contains( message, result.Output );
	}

	/// <summary>The plan's steps in order, as 'name&lt;dependency'.</summary>
	private static string[] Steps()
		=> JsonSerializer.Deserialize<JsonElement[]>( Succeed( "plan", "list-steps", "--plan", "orders", "-o", "json" ) )!
			.Select( x => $"{x.GetProperty( "name" ).GetString()}<{string.Join( ",", x.GetProperty( "dependencies" ).EnumerateArray().Select( d => d.GetString() ) )}" )
			.ToArray();

	private static string[] PlanDependencies( string plan )
		=> JsonSerializer.Deserialize<JsonElement>( Succeed( "plan", "--name", plan, "-o", "json" ) )
			.GetProperty( "dependencies" )
			.EnumerateArray()
			.Select( x => x.GetString()! )
			.ToArray();

	[Fact]
	public void PlanUpdateChangesDescriptionAndDependencies()
	{
		Succeed( "plan", "create", "--name", "audit" );

		Succeed( "plan", "update", "--name", "orders", "--description", "orders and lines", "--require-plans", "base" );

		Assert.Contains( "orders and lines", Succeed( "plan", "--name", "orders" ) );
		Assert.Equal( [ "base" ], PlanDependencies( "orders" ) );

		Succeed( "plan", "update", "--name", "orders", "--add-dependency", "audit", "--remove-dependency", "base" );
		Assert.Equal( [ "audit" ], PlanDependencies( "orders" ) );

		Succeed( "plan", "update", "--name", "orders", "--require-plans", "" );
		Assert.Empty( PlanDependencies( "orders" ) );
	}

	[Theory]
	[InlineData( "nothing to change", "plan", "update", "--name", "orders" )]
	[InlineData( "cannot be combined", "plan", "update", "--name", "orders", "--require-plans", "base", "--add-dependency", "base" )]
	[InlineData( "does not depend on 'base'", "plan", "update", "--name", "orders", "--remove-dependency", "base" )]
	[InlineData( "plan 'missing' does not exist", "plan", "update", "--name", "orders", "--add-dependency", "missing" )]
	[InlineData( "--description is required", "release", "update", "--name", "1.0" )]
	[InlineData( "--description is required", "plan", "update-step", "--plan", "orders", "--name", "a" )]
	[InlineData( "differ from the current name by more than case", "plan", "rename-step", "--plan", "orders", "--name", "a", "--new-name", "A" )]
	[InlineData( "a step with this name already exists", "plan", "rename-step", "--plan", "orders", "--name", "a", "--new-name", "b" )]
	[InlineData( "specify either --before <step> or --after <step>", "plan", "move-step", "--plan", "orders", "--name", "a" )]
	[InlineData( "specify either --before <step> or --after <step>", "plan", "move-step", "--plan", "orders", "--name", "a", "--before", "b", "--after", "c" )]
	[InlineData( "next to itself", "plan", "move-step", "--plan", "orders", "--name", "a", "--after", "A" )]
	[InlineData( "step 'missing' does not exist in plan 'orders'", "plan", "move-step", "--plan", "orders", "--name", "a", "--after", "missing" )]
	[InlineData( "a step requires at most one step", "plan", "add-step", "--plan", "orders", "--name", "d", "--requires", "a,b" )]
	public void BadEditsAreRefused( string message, params string[] args ) => Refused( message, args );

	[Fact]
	public void PlanUpdateRefusesACycle()
	{
		Succeed( "plan", "update", "--name", "orders", "--require-plans", "base" );

		Refused(
			"plan 'base' would depend on itself: base -> orders -> base",
			"plan", "update", "--name", "base", "--add-dependency", "orders"
		);
	}

	[Fact]
	public void EditsAreRefusedInAFinalizedRelease()
	{
		Succeed( "release", "create", "--name", "1.0", "--plans", "orders" );
		Succeed( "release", "update", "--name", "1.0", "--description", "first cut" );
		Assert.Contains( "first cut", Succeed( "release", "list" ) );

		Succeed( "release", "finalize", "--name", "1.0" );

		const string plan = "the plan belongs to a finalized release and can no longer be changed";

		Refused( "the release is finalized and can no longer be changed", "release", "update", "--name", "1.0", "--description", "x" );
		Refused( plan, "plan", "update", "--name", "orders", "--description", "x" );
		Refused( plan, "plan", "update-step", "--plan", "orders", "--name", "a", "--description", "x" );
		Refused( plan, "plan", "rename-step", "--plan", "orders", "--name", "a", "--new-name", "z" );
		Refused( plan, "plan", "move-step", "--plan", "orders", "--name", "a", "--after", "c" );
	}

	[Fact]
	public void UpdateStepChangesItsDescription()
	{
		Succeed( "plan", "update-step", "--plan", "orders", "--name", "b", "--description", "adds the index" );

		Assert.Contains( "adds the index", Succeed( "plan", "list-steps", "--plan", "orders" ) );
	}

	[Fact]
	public void RenameStepPrintsTheNewPathAndKeepsTheChain()
	{
		Assert.Equal( "plans/orders/steps/middle", Succeed( "plan", "rename-step", "--plan", "orders", "--name", "b", "--new-name", "middle" ) );

		Assert.Equal( [ "a<", "middle<a", "c<middle" ], Steps() );
		Assert.True( File.Exists( Path.Combine( _root, "plans", "orders", "steps", "middle", "deploy.sql" ) ) );
	}

	[Fact]
	public void MoveStepPrintsEachChangedDependency()
	{
		Assert.Equal(
			"c: requires a (was b)\nb: requires c (was a)",
			Succeed( "plan", "move-step", "--plan", "orders", "--name", "b", "--after", "c" )
		);

		Assert.Equal( [ "a<", "c<a", "b<c" ], Steps() );

		Assert.Equal(
			"b: requires nothing (was c)\na: requires b (was nothing)",
			Succeed( "plan", "move-step", "--plan", "orders", "--name", "b", "--before", "a" )
		);

		Assert.Equal( [ "b<", "a<b", "c<a" ], Steps() );

		Assert.Equal( "step 'b' is already there", Succeed( "plan", "move-step", "--plan", "orders", "--name", "b", "--before", "a" ) );
	}
}
