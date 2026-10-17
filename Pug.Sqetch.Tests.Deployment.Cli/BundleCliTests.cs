using System.Formats.Tar;
using System.Reflection;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pug.Sqetch.Bundling;
using Pug.Sqetch.Cli.Commands.Deployment;
using Pug.Sqetch.Deployment.Cli;
using Pug.Sqetch.Models;
using Pug.Sqetch.Tests.Stores.FileSystem;
using Spectre.Console.Cli.Testing;

namespace Pug.Sqetch.Tests.Deployment.Cli;

/// <summary>
/// Drives 'sqetch-deploy bundle' through <see cref="CommandAppTester"/> hosting the production
/// configuration. Projects are built through the engine and passed with <c>--project</c>, and
/// outputs go to absolute paths, so no test changes the process-wide current directory — the one
/// test that has to is <see cref="BundleDefaultOutputCliTests"/>.
/// </summary>
public class BundleCliTests : IDisposable
{
	private readonly TempProject _temp = TempProject.Create();
	private readonly string _out;

	public BundleCliTests()
	{
		_out = Path.Combine( Path.GetTempPath(), "sqetch-bundle-cli-tests", Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( _out );
	}

	public void Dispose()
	{
		_temp.Dispose();

		try
		{
			Directory.Delete( _out, recursive: true );
		}
		catch( IOException )
		{
		}
	}

	internal static CommandAppResult Run( params string[] args )
	{
		CommandAppTester tester = new ( SqetchDeployApp.CreateDefaultRegistrar() );

		tester.Configure( SqetchDeployApp.Configure );

		return tester.Run( args );
	}

	private string Out( string name ) => Path.Combine( _out, name );

	private CommandAppResult Bundle( params string[] args )
		=> Run( ["bundle", "--project", _temp.Root, ..args] );

	private void Populate( bool withJournaling = true )
		=> BundleTestProjects.Populate( _temp, project =>
		{
			BundleTestProjects.AddFinalizedRelease( _temp, project );

			if( withJournaling )
				BundleTestProjects.SetJournaling( project );
		} );

	private static JsonDocument ReadManifest( ZipArchive archive )
		=> JsonDocument.Parse( new StreamReader( archive.GetEntry( "manifest.json" )!.Open() ).ReadToEnd() );

	[Fact]
	public void BundleDefaultsToFinalizedZipAndPrintsThePath()
	{
		Populate();

		string path = Out( "demo.zip" );

		CommandAppResult result = Bundle( "--bundle-output-path", path );

		Assert.Equal( DeployExitCodes.Success, result.ExitCode );
		Assert.Equal( path, result.Output );

		using ZipArchive archive = ZipFile.OpenRead( path );

		Assert.Equal(
			[
				"META-DATA",
				"manifest.json",
				"plans/table/steps/create/deploy.sql",
				"plans/table/steps/create/verify.sql",
				"plans/table/steps/create/rollback.sql"
			],
			archive.Entries.Select( x => x.FullName ).Where( x => !x.StartsWith( "journaling/" ) ).ToArray() );

		// the project's journaling SQL travels with the bundle, between the manifest and the plans
		Assert.Equal( 15, archive.Entries.Count( x => x.FullName.StartsWith( "journaling/" ) ) );
		Assert.Equal(
			"select 'PrepareJournal'",
			new StreamReader( archive.GetEntry( "journaling/PrepareJournal.sql" )!.Open() ).ReadToEnd().Trim() );

		using JsonDocument manifest = ReadManifest( archive );

		Assert.Equal( "finalized", manifest.RootElement.GetProperty( "selection" ).GetString() );
		Assert.Equal( "test-project", manifest.RootElement.GetProperty( "project" ).GetProperty( "name" ).GetString() );
		Assert.Equal( "2026.07", manifest.RootElement.GetProperty( "plans" )[0].GetProperty( "release" ).GetString() );
	}

	[Fact]
	public void TheBundleCarriesMetadataNamingThisToolAndTheMomentOfBundling()
	{
		Populate();

		DateTime before = TruncateToSecond( DateTime.UtcNow );

		Assert.Equal( DeployExitCodes.Success, Bundle( "--bundle-output-path", Out( "meta.zip" ) ).ExitCode );

		DateTime after = DateTime.UtcNow;

		using ZipArchive archive = ZipFile.OpenRead( Out( "meta.zip" ) );

		// the first entry, so a streaming reader meets it before anything else
		Assert.Equal( "META-DATA", archive.Entries[0].FullName );

		string text = new StreamReader( archive.GetEntry( "META-DATA" )!.Open() ).ReadToEnd();
		BundleMetadata metadata = BundleMetadata.Parse( text );

		Assert.Equal( 1, metadata.Format );
		Assert.Equal( "default", metadata.Layout );
		Assert.Equal( "sqetch-deploy", metadata.Bundler );
		Assert.Equal(
			typeof(SqetchDeployApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
			metadata.BundlerVersion );
		Assert.Equal( DateTimeKind.Utc, metadata.Bundled.Kind );
		Assert.InRange( metadata.Bundled, before, after );

		// the metadata replaces the manifest's old generation timestamp as the one instant
		using JsonDocument manifest = ReadManifest( archive );

		Assert.False( manifest.RootElement.TryGetProperty( "generated", out _ ) );
	}

	private static DateTime TruncateToSecond( DateTime value )
		=> new ( value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind );

	/// <summary>
	/// Bundling only reads, and 'sqetch-deploy' never looks for a project user: neither the project
	/// nor the directory the command runs in carries a '.sqetch-user', and the bundle is made.
	/// </summary>
	[Fact]
	public void BundlingNeedsNoProjectUser()
	{
		Populate();

		Assert.False( File.Exists( Path.Combine( _temp.Root, ".sqetch-user" ) ) );

		CommandAppResult result = Bundle( "--bundle-output-path", Out( "anonymous.zip" ) );

		Assert.Equal( DeployExitCodes.Success, result.ExitCode );
		Assert.True( File.Exists( Out( "anonymous.zip" ) ) );
	}

	[Fact]
	public void TestSelectionIncludesUnreleasedPlansAfterReleasedOnes()
	{
		BundleTestProjects.Populate( _temp, project =>
		{
			BundleTestProjects.AddFinalizedRelease( _temp, project );
			BundleTestProjects.SetJournaling( project );

			project.Add( new PlanDefinition( "pending", "", [] ) );
			BundleTestProjects.SeedScripts(
				_temp, project.Add( new StepDefinition( "pending", "later", "", [] ), "pending" ), "later" );
		} );

		CommandAppResult result = Bundle( "--test", "--bundle-output-path", Out( "test.zip" ) );

		Assert.Equal( DeployExitCodes.Success, result.ExitCode );
		Assert.Equal( Out( "test.zip" ), result.Output );

		using ZipArchive archive = ZipFile.OpenRead( Out( "test.zip" ) );
		using JsonDocument manifest = ReadManifest( archive );

		JsonElement[] plans = manifest.RootElement.GetProperty( "plans" ).EnumerateArray().ToArray();

		Assert.Equal( ["table", "pending"], plans.Select( x => x.GetProperty( "name" ).GetString()! ).ToArray() );
		Assert.Equal( "", plans[1].GetProperty( "release" ).GetString() );
		Assert.Equal( "test", manifest.RootElement.GetProperty( "selection" ).GetString() );
	}

	[Fact]
	public void SinceBundlesOnlyTheReleasesAfterTheNamedOne()
	{
		BundleTestProjects.Populate( _temp, project =>
		{
			BundleTestProjects.AddFinalizedRelease( _temp, project );
			BundleTestProjects.SetJournaling( project );

			project.Add( new PlanDefinition( "index", "", [] ) );
			BundleTestProjects.SeedScripts(
				_temp, project.Add( new StepDefinition( "index", "create", "", [] ), "index" ), "create" );

			project.CreateRelease( new ReleaseDefinition( "2026.08", "", "2026.07" ), ["index"] );
			project.FinalizeRelease( "2026.08" );
		} );

		CommandAppResult result = Bundle( "--since", "2026.07", "--bundle-output-path", Out( "since.zip" ) );

		Assert.Equal( DeployExitCodes.Success, result.ExitCode );
		Assert.Equal( Out( "since.zip" ), result.Output );

		using ZipArchive archive = ZipFile.OpenRead( Out( "since.zip" ) );
		using JsonDocument manifest = ReadManifest( archive );

		JsonElement[] releases = manifest.RootElement.GetProperty( "releases" ).EnumerateArray().ToArray();

		Assert.Equal( ["2026.08"], releases.Select( x => x.GetProperty( "name" ).GetString()! ).ToArray() );
		Assert.Equal( "2026.07", releases[0].GetProperty( "dependency" ).GetString() );

		JsonElement[] plans = manifest.RootElement.GetProperty( "plans" ).EnumerateArray().ToArray();

		Assert.Equal( ["index"], plans.Select( x => x.GetProperty( "name" ).GetString()! ).ToArray() );
	}

	[Fact]
	public void SinceWithAnUnknownReleaseFailsWithFriendlyError()
	{
		Populate();

		CommandAppResult result = Bundle( "--since", "nope", "--bundle-output-path", Out( "since.zip" ) );

		Assert.Equal( DeployExitCodes.ProjectNotBundleable, result.ExitCode );
		Assert.Contains( "does not exist", result.Output );
		Assert.False( File.Exists( Out( "since.zip" ) ) );
	}

	[Fact]
	public void DirectoryBundleMaterializesTheTree()
	{
		Populate();

		CommandAppResult result = Bundle( "--bundle-type", "directory", "--bundle-output-path", Out( "deploy" ) );

		Assert.Equal( DeployExitCodes.Success, result.ExitCode );
		Assert.Equal( Out( "deploy" ), result.Output );

		Assert.True( File.Exists( Path.Combine( Out( "deploy" ), "META-DATA" ) ) );
		Assert.True( File.Exists( Path.Combine( Out( "deploy" ), "manifest.json" ) ) );
		Assert.True( File.Exists(
			Path.Combine( Out( "deploy" ), "plans", "table", "steps", "create", "deploy.sql" ) ) );
	}

	[Fact]
	public void TarGzBundleIsReadable()
	{
		Populate();

		Assert.Equal(
			DeployExitCodes.Success,
			Bundle( "--bundle-type", "tar.gz", "--bundle-output-path", Out( "b.tar.gz" ) ).ExitCode );

		using FileStream file = File.OpenRead( Out( "b.tar.gz" ) );
		using GZipStream gzip = new ( file, CompressionMode.Decompress );
		using TarReader reader = new ( gzip );

		// the metadata leads, then the manifest, so streaming consumers see both before the payload
		Assert.Equal( "META-DATA", reader.GetNextEntry()!.Name );
		Assert.Equal( "manifest.json", reader.GetNextEntry()!.Name );
	}

	[Fact]
	public void MissingStepScriptFailsWithoutCreatingOutput()
	{
		Populate();

		File.Delete( Directory.GetFiles( _temp.Root, "verify.sql", SearchOption.AllDirectories ).Single() );

		CommandAppResult result = Bundle( "--bundle-output-path", Out( "broken.zip" ) );

		Assert.Equal( DeployExitCodes.ProjectNotBundleable, result.ExitCode );
		Assert.Contains( "missing script(s): verify", result.Output );
		Assert.False( File.Exists( Out( "broken.zip" ) ) );
	}

	[Fact]
	public void NothingToBundleFailsWithFriendlyError()
	{
		BundleTestProjects.Populate( _temp, project =>
		{
			project.Add( new PlanDefinition( "unreleased-only", "", [] ) );
			BundleTestProjects.SetJournaling( project );
		} );

		CommandAppResult result = Bundle( "--bundle-output-path", Out( "empty.zip" ) );

		Assert.Equal( DeployExitCodes.ProjectNotBundleable, result.ExitCode );
		Assert.Contains( "nothing to bundle", result.Output );
		Assert.False( File.Exists( Out( "empty.zip" ) ) );
	}

	/// <summary>
	/// A bundle journals through the project's own SQL, so bundling without it is refused — and the
	/// error names every unset slot, since a maintainer setting them up wants the whole list.
	/// </summary>
	[Fact]
	public void BundlingWithoutJournalingStatementsIsRefusedNamingThem()
	{
		Populate( withJournaling: false );

		CommandAppResult result = Bundle( "--bundle-output-path", Out( "unjournaled.zip" ) );

		Assert.Equal( DeployExitCodes.ProjectNotBundleable, result.ExitCode );
		Assert.Contains( "DeployingRelease", result.Output );
		Assert.Contains( "GetDeployedPlans", result.Output );
		Assert.Empty( Directory.GetFiles( _out ) );
	}

	[Fact]
	public void ContradictingSelectionsAreRejected()
	{
		Populate();

		CommandAppResult result = Bundle( "--test", "--finalized" );

		Assert.Equal( DeployExitCodes.UsageError, result.ExitCode );
		Assert.Contains( "either --test or --finalized", result.Output );
	}

	[Fact]
	public void UnknownBundleTypeIsAUsageError()
	{
		Populate();

		CommandAppResult result = Bundle( "--bundle-type", "rar", "--bundle-output-path", Out( "x.rar" ) );

		Assert.Equal( DeployExitCodes.UsageError, result.ExitCode );
		Assert.Contains( "Unknown bundle type 'rar'", result.Output );
		Assert.Contains( "zip", result.Output );
		Assert.False( File.Exists( Out( "x.rar" ) ) );
	}

	/// <summary>
	/// Strict parsing is off app-wide so 'deploy' can read a driver's switches out of the leftovers,
	/// so a typo'd option reaches the command and has to be refused there.
	/// </summary>
	[Fact]
	public void AnUnknownOptionIsRefusedAndNothingIsBundled()
	{
		Populate();

		CommandAppResult result = Bundle( "--bundel-output-path", Out( "typo.zip" ), "--bundle-output-path", Out( "real.zip" ) );

		Assert.Equal( DeployExitCodes.UsageError, result.ExitCode );
		Assert.Contains( "unknown option '--bundel-output-path'", result.Output );
		Assert.False( File.Exists( Out( "real.zip" ) ) );
	}

	[Fact]
	public void ExistingOutputTargetIsRefusedIntact()
	{
		Populate();

		File.WriteAllText( Out( "taken.zip" ), "keep" );

		CommandAppResult result = Bundle( "--bundle-output-path", Out( "taken.zip" ) );

		Assert.Equal( DeployExitCodes.BundleOutputFailed, result.ExitCode );
		Assert.Contains( "already exists", result.Output );
		Assert.Contains( Out( "taken.zip" ), result.Output );
		Assert.Equal( "keep", File.ReadAllText( Out( "taken.zip" ) ) );
	}

	[Fact]
	public void ExistingOutputDirectoryIsRefusedIntact()
	{
		Populate();

		Directory.CreateDirectory( Out( "taken" ) );
		File.WriteAllText( Path.Combine( Out( "taken" ), "keep.txt" ), "keep" );

		CommandAppResult result = Bundle( "--bundle-type", "directory", "--bundle-output-path", Out( "taken" ) );

		Assert.Equal( DeployExitCodes.BundleOutputFailed, result.ExitCode );
		Assert.Equal( "keep", File.ReadAllText( Path.Combine( Out( "taken" ), "keep.txt" ) ) );
	}

	[Fact]
	public void AProjectDirectoryThatDoesNotExistIsUnreadable()
	{
		string missing = Out( "nowhere" );

		CommandAppResult result = Run( "bundle", "--project", missing, "--bundle-output-path", Out( "x.zip" ) );

		Assert.Equal( DeployExitCodes.ProjectUnreadable, result.ExitCode );
		Assert.Contains( "is not a Sqetch project directory", result.Output );
		Assert.False( File.Exists( Out( "x.zip" ) ) );
	}

	[Fact]
	public void AProjectOfANewerFormatIsUnreadable()
	{
		Populate();

		string file = Path.Combine( _temp.Root, "sqetch-project.json" );
		JsonNode document = JsonNode.Parse( File.ReadAllText( file ) )!;

		document["format"] = 2;
		File.WriteAllText( file, document.ToJsonString() );

		CommandAppResult result = Bundle( "--bundle-output-path", Out( "x.zip" ) );

		Assert.Equal( DeployExitCodes.ProjectUnreadable, result.ExitCode );
		Assert.Contains( "format 2", result.Output );
		Assert.False( File.Exists( Out( "x.zip" ) ) );
	}

	[Fact]
	public void SinceNamingAnOpenReleaseLeavesNothingToBundle()
	{
		BundleTestProjects.Populate( _temp, project =>
		{
			BundleTestProjects.AddFinalizedRelease( _temp, project );
			BundleTestProjects.SetJournaling( project );

			project.Add( new PlanDefinition( "index", "", [] ) );
			BundleTestProjects.SeedScripts(
				_temp, project.Add( new StepDefinition( "index", "create", "", [] ), "index" ), "create" );

			project.CreateRelease( new ReleaseDefinition( "2026.08", "", "2026.07" ), ["index"] );
		} );

		CommandAppResult result = Bundle( "--since", "2026.08", "--bundle-output-path", Out( "open.zip" ) );

		Assert.Equal( DeployExitCodes.ProjectNotBundleable, result.ExitCode );
		Assert.False( File.Exists( Out( "open.zip" ) ) );
	}
}
