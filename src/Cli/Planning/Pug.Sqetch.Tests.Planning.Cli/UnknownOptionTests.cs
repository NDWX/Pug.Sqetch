using Spectre.Console.Cli;
using Spectre.Console.Cli.Testing;

namespace Pug.Sqetch.Tests.Planning.Cli;

/// <summary>
/// 'sqetch' parses strictly: an option no command defines is refused before the command runs, rather
/// than collected and ignored, which would let a typo succeed without its effect.
/// </summary>
[Collection( "cli" )]
public sealed class UnknownOptionTests : IDisposable
{
	private readonly string _root;
	private readonly string _originalDirectory;

	public UnknownOptionTests()
	{
		_root = Path.Combine( Path.GetTempPath(), "sqetch-cli-tests", Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( _root );

		_originalDirectory = Directory.GetCurrentDirectory();
		Directory.SetCurrentDirectory( _root );

		Assert.Equal( 0, Run( "project", "user", "tester", "tester@example.com" ).ExitCode );
		Assert.Equal( 0, Run( "project", "init", "demo" ).ExitCode );
		Assert.Equal( 0, Run( "plan", "create", "--name", "orders" ).ExitCode );
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

	/// <summary>
	/// The parse error's text. A parse error reaches the central handler without a resolver, so the
	/// handler prints it to the process's console rather than the tester's: the exception is where a
	/// test can read it.
	/// </summary>
	private static string ParseError( params string[] args )
		=> SqetchTester.Create().RunAndCatch<CommandParseException>( args ).Exception.Message;

	[Fact]
	public void AMistypedOptionCreatesNothing()
	{
		CommandAppResult result = Run( "plan", "create", "--name", "lines", "--requires-plans", "orders" );

		Assert.Equal( 1, result.ExitCode );
		Assert.Contains( "requires-plans", ParseError( "plan", "create", "--name", "lines", "--requires-plans", "orders" ) );
		Assert.False( Directory.Exists( Path.Combine( _root, "plans", "lines" ) ) );
	}

	[Theory]
	[InlineData( "release", "list", "--bogus" )]
	[InlineData( "plan", "list", "--bogus", "x" )]
	[InlineData( "plan", "update", "--name", "orders", "--description", "x", "--bogus" )]
	[InlineData( "journaling", "list", "--bogus" )]
	[InlineData( "project", "reindex", "--bogus" )]
	public void AnUnknownOptionIsRefused( params string[] args )
	{
		CommandAppResult result = Run( args );

		Assert.Equal( 1, result.ExitCode );
		Assert.Contains( "bogus", ParseError( args ) );
	}

	/// <summary>
	/// 'plan --name' is the plan branch's default command. Spectre refuses an unknown option there
	/// too, but names the default command's first option rather than the unknown one — its parser, not
	/// ours — so only the refusal is pinned.
	/// </summary>
	[Fact]
	public void AnUnknownOptionOfTheBranchDefaultCommandIsRefused()
	{
		Assert.Equal( 1, Run( "plan", "--name", "orders", "--bogus" ).ExitCode );
		Assert.Equal( 0, Run( "plan", "--name", "orders" ).ExitCode );
	}

	[Fact]
	public void ARefusedUpdateChangesNothing()
	{
		Assert.Equal( 1, Run( "plan", "update", "--name", "orders", "--description", "changed", "--bogus" ).ExitCode );

		Assert.DoesNotContain( "changed", Run( "plan", "--name", "orders" ).Output );
	}
}
