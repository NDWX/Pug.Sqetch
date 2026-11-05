using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;

namespace Pug.Sqetch.Tests.Planning.Cli;

/// <summary>
/// 'sqetch plan graph' on a small project: release 1.0 holds <c>base</c>, release 1.1 holds
/// <c>orders</c> (requiring <c>base</c>) and <c>invoices</c> (requiring <c>orders</c>), and the
/// unreleased <c>reports</c> requires <c>invoices</c> and <c>orders</c>. Outputs are pinned whole,
/// since another program parses the Mermaid and DOT forms.
/// </summary>
[Collection( "cli" )]
public sealed class PlanGraphTests : IDisposable
{
	private readonly string _root;
	private readonly string _originalDirectory;

	public PlanGraphTests()
	{
		_root = Path.Combine( Path.GetTempPath(), "sqetch-cli-tests", Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( _root );

		_originalDirectory = Directory.GetCurrentDirectory();
		Directory.SetCurrentDirectory( _root );

		Succeed( "project", "user", "tester", "tester@example.com" );
		Succeed( "project", "init", "demo" );
		Succeed( "release", "create", "--name", "1.0" );
		Succeed( "release", "create", "--name", "1.1", "--depends-on", "1.0" );
		Succeed( "plan", "create", "--name", "base", "--release", "1.0" );
		Succeed( "plan", "create", "--name", "orders", "--require-plans", "base", "--release", "1.1" );
		Succeed( "plan", "create", "--name", "invoices", "--require-plans", "orders", "--release", "1.1" );
		Succeed( "plan", "create", "--name", "reports", "--require-plans", "invoices,orders" );
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

	private static void Succeed( params string[] args ) => Assert.Equal( 0, Run( args ).ExitCode );

	private static string[] Graph( params string[] options )
	{
		CommandAppResult result = Run( [ "plan", "graph", .. options ] );

		Assert.Equal( 0, result.ExitCode );

		return result.Output.Split( '\n' ).Select( x => x.TrimEnd( '\r' ) ).ToArray();
	}

	[Fact]
	public void TextIsTheDefault()
	{
		Assert.Equal(
			[
				"Release 1.0",
				"  base",
				"",
				"Release 1.1",
				"  orders: requires base (1.0)",
				"  invoices: requires orders (1.1)",
				"",
				"Unreleased",
				"  reports: requires invoices (1.1), orders (1.1)"
			],
			Graph()
		);
	}

	[Fact]
	public void MermaidGroupsReleasesAsSubgraphsInLineageOrder()
	{
		Assert.Equal(
			[
				"flowchart TD",
				"    subgraph r1[\"1.0\"]",
				"        p1[\"base\"]",
				"    end",
				"    subgraph r2[\"1.1\"]",
				"        p2[\"orders\"]",
				"        p3[\"invoices\"]",
				"    end",
				"    subgraph u[\"(unreleased)\"]",
				"        p4[\"reports\"]",
				"    end",
				"    p1 --> p2",
				"    p2 --> p3",
				"    p3 --> p4",
				"    p2 --> p4"
			],
			Graph( "--format", "mermaid" )
		);
	}

	[Fact]
	public void DotGroupsReleasesAsClusters()
	{
		Assert.Equal(
			[
				"digraph plans {",
				"    subgraph \"cluster_1\" {",
				"        label=\"1.0\";",
				"        \"base\";",
				"    }",
				"    subgraph \"cluster_2\" {",
				"        label=\"1.1\";",
				"        \"orders\";",
				"        \"invoices\";",
				"    }",
				"    subgraph \"cluster_3\" {",
				"        label=\"(unreleased)\";",
				"        \"reports\";",
				"    }",
				"    \"base\" -> \"orders\";",
				"    \"orders\" -> \"invoices\";",
				"    \"invoices\" -> \"reports\";",
				"    \"orders\" -> \"reports\";",
				"}"
			],
			Graph( "--format", "DOT" )
		);
	}

	[Fact]
	public void AReleasesDependencyOnAnEarlierReleaseIsDrawnDashed()
	{
		Assert.Equal(
			[
				"flowchart TD",
				"    subgraph r1[\"1.1\"]",
				"        p1[\"orders\"]",
				"        p2[\"invoices\"]",
				"    end",
				"    x1[\"base (1.0)\"]:::external",
				"    x1 --> p1",
				"    p1 --> p2",
				"    classDef external stroke-dasharray: 5 5"
			],
			Graph( "-r", "1.1", "--format", "mermaid" )
		);

		Assert.Equal(
			[
				"digraph plans {",
				"    subgraph \"cluster_1\" {",
				"        label=\"1.1\";",
				"        \"orders\";",
				"        \"invoices\";",
				"    }",
				"    \"base\" [label=\"base (1.0)\", style=dashed];",
				"    \"base\" -> \"orders\";",
				"    \"orders\" -> \"invoices\";",
				"}"
			],
			Graph( "-r", "1.1", "--format", "dot" )
		);
	}

	[Fact]
	public void UnreleasedPlansAloneNameTheReleasesTheyRequire()
	{
		Assert.Equal(
			[ "Unreleased", "  reports: requires invoices (1.1), orders (1.1)" ],
			Graph( "--unreleased" )
		);
	}

	[Fact]
	public void AReleaseAndTheUnreleasedPlansCanBeShownTogether()
	{
		Assert.Equal(
			[
				"Release 1.1",
				"  orders: requires base (1.0)",
				"  invoices: requires orders (1.1)",
				"",
				"Unreleased",
				"  reports: requires invoices (1.1), orders (1.1)"
			],
			Graph( "-r", "1.1", "--unreleased" )
		);
	}

	[Fact]
	public void FromReleaseShowsThatReleaseAndEverythingAfterIt()
	{
		Succeed( "release", "create", "--name", "1.2", "--depends-on", "1.1" );
		Succeed( "plan", "create", "--name", "audit", "--require-plans", "base", "--release", "1.2" );

		Assert.Equal(
			[
				"Release 1.1",
				"  orders: requires base (1.0)",
				"  invoices: requires orders (1.1)",
				"",
				"Release 1.2",
				"  audit: requires base (1.0)",
				"",
				"Unreleased",
				"  reports: requires invoices (1.1), orders (1.1)"
			],
			Graph( "--from-release", "1.1" )
		);

		Assert.Equal(
			[
				"flowchart TD",
				"    subgraph r1[\"1.1\"]",
				"        p1[\"orders\"]",
				"        p2[\"invoices\"]",
				"    end",
				"    subgraph r2[\"1.2\"]",
				"        p3[\"audit\"]",
				"    end",
				"    subgraph u[\"(unreleased)\"]",
				"        p4[\"reports\"]",
				"    end",
				"    x1[\"base (1.0)\"]:::external",
				"    x1 --> p1",
				"    p1 --> p2",
				"    x1 --> p3",
				"    p2 --> p4",
				"    p1 --> p4",
				"    classDef external stroke-dasharray: 5 5"
			],
			Graph( "--from-release", "1.1", "--format", "mermaid" )
		);
	}

	[Fact]
	public void AnEmptyReleaseAskedForByNameIsShown()
	{
		Succeed( "release", "create", "--name", "1.2", "--depends-on", "1.1" );

		Assert.Equal( [ "Release 1.2" ], Graph( "-r", "1.2" ) );
		Assert.DoesNotContain( "Release 1.2", Graph() );
	}

	[Fact]
	public void AFinalizedReleaseIsPartOfTheWholeProject()
	{
		Succeed( "release", "finalize", "--name", "1.0" );

		Assert.Equal( "Release 1.0", Graph()[0] );
	}

	[Theory]
	[InlineData( "--format svg", "--format must be 'text', 'mermaid' or 'dot'" )]
	[InlineData( "--from-release 1.0 -r 1.1", "--from-release cannot be combined with --release or --unreleased" )]
	[InlineData( "--from-release 1.0 --unreleased", "--from-release cannot be combined with --release or --unreleased" )]
	[InlineData( "--from-release missing", "release 'missing' does not exist" )]
	[InlineData( "-r missing", "release 'missing' does not exist" )]
	public void BadOptionsAreRefused( string options, string message )
	{
		CommandAppResult result = Run( [ "plan", "graph", .. options.Split( ' ' ) ] );

		Assert.Equal( 1, result.ExitCode );
		Assert.Contains( message, result.Output );
	}

	/// <summary>
	/// Output piped to another program is where Spectre wraps at 80 columns; a wrapped line would
	/// break a Mermaid or DOT statement in two.
	/// </summary>
	[Fact]
	public void ALongPlanNameStaysOnOneLine()
	{
		string name = new( 'n', 128 );

		Succeed( "plan", "create", "--name", name, "--require-plans", "reports" );

		Assert.Contains( $"  {name}: requires reports (unreleased)", NarrowGraph( "--unreleased" ) );
		Assert.Contains( $"        p5[\"{name}\"]", NarrowGraph( "--format", "mermaid" ) );
	}

	/// <summary>The graph as written to an 80-column, non-interactive console: a redirected stdout.</summary>
	private static string[] NarrowGraph( params string[] options )
	{
		TestConsole console = new TestConsole().Width( 80 );

		Assert.Equal( 0, SqetchTester.Create( console: console ).Run( [ "plan", "graph", .. options ] ).ExitCode );

		return console.Output.Split( '\n' ).Select( x => x.TrimEnd( '\r' ) ).ToArray();
	}
}
