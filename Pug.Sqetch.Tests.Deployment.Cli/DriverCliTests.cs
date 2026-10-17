using System.Text.Json;
using Pug.Sqetch.Bundling;
using Pug.Sqetch.Bundling.Layouts;
using Pug.Sqetch.Cli.Commands.Deployment;
using Pug.Sqetch.Deployment;
using Pug.Sqetch.Deployment.Cli;
using Pug.Sqetch.DatabaseDriver;
using Spectre.Console.Cli.Testing;

namespace Pug.Sqetch.Tests.Deployment.Cli;

/// <summary>
/// The 'drivers' branch, which answers from the driver registry alone — so unlike every other
/// sqetch-deploy command these tests need neither a bundle nor a database.
/// </summary>
public class DriverCliTests
{
	private readonly DatabaseDriverRegistry _drivers = new ();

	public DriverCliTests()
	{
		_drivers.Register( "sqlite", () => new FakeFactory( "sqlite", [["file"], ["connection-string"]] ) );

		_drivers.Register(
			"pg", () => new FakeFactory( "pg", [["host", "database"], ["connection-string"]] ) );
	}

	[Fact]
	public void DriverListNamesEveryRegisteredDriver()
	{
		CommandAppResult result = Run( "drivers", "list" );

		Assert.Equal( DeployExitCodes.Success, result.ExitCode );

		// sorted, because the registry's own order is the host's wiring order
		Assert.Equal( ["pg", "sqlite"], Lines( result.Output ) );
	}

	/// <summary>
	/// The sets come first because required-ness is a fact about sets of parameters, not about any
	/// one of them: 'host' is required only in the company of 'database', and not at all if
	/// 'connection-string' is given.
	/// </summary>
	[Fact]
	public void DriverParametersPutsTheRequiredSetsBeforeTheDefinitions()
	{
		CommandAppResult result = Run( "drivers", "parameters", "pg" );

		Assert.Equal( DeployExitCodes.Success, result.ExitCode );

		Assert.Equal(
			[
				"pg/required-sets\t1\t--pg-host --pg-database",
				"pg/required-sets\t2\t--pg-connection-string",
				"pg/parameters\t--pg-host\thost of pg",
				"pg/parameters\t--pg-database\tdatabase of pg",
				"pg/parameters\t--pg-connection-string\tconnection-string of pg"
			],
			Lines( result.Output ) );
	}

	[Fact]
	public void DriverParametersCoversEveryDriverWhenNoneIsNamed()
	{
		CommandAppResult result = Run( "drivers", "parameters", "-o", "json" );

		Assert.Equal( DeployExitCodes.Success, result.ExitCode );

		JsonElement[] drivers = JsonSerializer.Deserialize<JsonElement[]>( result.Output )!;

		Assert.Equal( ["pg", "sqlite"], drivers.Select( x => x.GetProperty( "driver" ).GetString() ) );

		Assert.Equal(
			[["--pg-host", "--pg-database"], ["--pg-connection-string"]],
			drivers[0].GetProperty( "requiredParameterSets" )
						.EnumerateArray()
						.Select( set => set.EnumerateArray().Select( x => x.GetString() ).ToArray() ) );
	}

	[Fact]
	public void ADriverRequiringNothingStillGetsItsSetsSection()
	{
		_drivers.Register( "loose", () => new FakeFactory( "loose", [] ) );

		CommandAppResult result = Run( "drivers", "parameters", "loose", "-o", "json" );

		Assert.Equal( DeployExitCodes.Success, result.ExitCode );

		JsonElement[] drivers = JsonSerializer.Deserialize<JsonElement[]>( result.Output )!;

		Assert.Empty( drivers[0].GetProperty( "requiredParameterSets" ).EnumerateArray() );
	}

	[Fact]
	public void AnUnknownDriverIsRefusedWithTheDriverCode()
	{
		CommandAppResult result = Run( "drivers", "parameters", "mysql" );

		Assert.Equal( DeployExitCodes.UnknownDriver, result.ExitCode );
		Assert.Contains( "unknown database driver 'mysql'", result.Output );
	}

	/// <summary>
	/// Strict parsing is off app-wide so the deploy command can read a driver's own switches out of
	/// the leftovers; without a guard of its own a listing command would accept a typo in silence.
	/// </summary>
	[Theory]
	[InlineData( "list" )]
	[InlineData( "parameters" )]
	public void AnUnrecognizedOptionIsNotSwallowed( string command )
	{
		CommandAppResult result = Run( "drivers", command, "--bogus" );

		Assert.Equal( DeployExitCodes.UsageError, result.ExitCode );
		Assert.Contains( "--bogus", result.Output );
	}

	private static string[] Lines( string output )
		=> output.Split( '\n', StringSplitOptions.RemoveEmptyEntries ).Select( x => x.TrimEnd( '\r' ) ).ToArray();

	private CommandAppResult Run( params string[] args )
	{
		TypeRegistrar registrar = new ();

		registrar.RegisterInstance( typeof(IDatabaseDriverRegistry), _drivers );
		registrar.RegisterInstance( typeof(IBundleTypeRegistry), new BundleTypeRegistry() );
		registrar.RegisterInstance( typeof(IBundleLayoutRegistry), new BundleLayoutRegistry() );
		registrar.RegisterInstance( typeof(BundlerIdentity), new BundlerIdentity( "sqetch-deploy", "0.1.0" ) );

		CommandAppTester tester = new ( registrar );

		tester.Configure( SqetchDeployApp.Configure );

		return tester.Run( args );
	}

	private sealed class FakeFactory(
		string name, ICollection<ICollection<string>> required, string? description = null )
		: IDatabaseDriverFactory
	{
		public string Name => name;

		public DatabaseDriverParametersDefinition GetParametersDefinition()
			=> new (
				required.SelectMany( x => x )
						.Distinct()
						.Select( x => new DatabaseDriverParameterDefinition( x, description ?? $"{x} of {name}" ) )
						.ToList(),
				required );

		public IDatabaseDriver Create( IDictionary<string, string> parameters, TimeSpan stepScriptTimeout )
			=> throw new NotSupportedException( "these tests never deploy" );
	}
}
