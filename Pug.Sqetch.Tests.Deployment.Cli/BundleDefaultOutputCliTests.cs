using Pug.Sqetch.Cli.Commands.Deployment;
using Pug.Sqetch.Tests.Stores.FileSystem;
using Spectre.Console.Cli.Testing;

namespace Pug.Sqetch.Tests.Deployment.Cli;

/// <summary>
/// Declares the "cwd" collection, which runs alone: its members switch the process-wide current
/// directory, which nothing running beside them may be reading.
/// </summary>
[CollectionDefinition( "cwd", DisableParallelization = true )]
public class CurrentDirectoryCollection;

/// <summary>
/// The default output name is relative to the current directory, so this is the one bundle test
/// that has to switch it — and so sits alone in the "cwd" collection.
/// </summary>
[Collection( "cwd" )]
public class BundleDefaultOutputCliTests : IDisposable
{
	private readonly TempProject _temp = TempProject.Create();
	private readonly string _out;
	private readonly string _originalDirectory;

	public BundleDefaultOutputCliTests()
	{
		_out = Path.Combine( Path.GetTempPath(), "sqetch-bundle-cwd-tests", Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( _out );

		_originalDirectory = Directory.GetCurrentDirectory();
		Directory.SetCurrentDirectory( _out );
	}

	public void Dispose()
	{
		Directory.SetCurrentDirectory( _originalDirectory );

		_temp.Dispose();

		try
		{
			Directory.Delete( _out, recursive: true );
		}
		catch( IOException )
		{
		}
	}

	[Fact]
	public void WithoutAnOutputPathTheBundleIsNamedAfterTheProjectInTheCurrentDirectory()
	{
		BundleTestProjects.Populate( _temp, project =>
		{
			BundleTestProjects.AddFinalizedRelease( _temp, project );
			BundleTestProjects.SetJournaling( project );
		} );

		CommandAppResult result = BundleCliTests.Run( "bundle", "--project", _temp.Root );

		Assert.Equal( DeployExitCodes.Success, result.ExitCode );
		Assert.Matches( @"^test-project-\d{14}\.zip$", result.Output );
		Assert.True( File.Exists( Path.Combine( _out, result.Output ) ) );
	}
}
