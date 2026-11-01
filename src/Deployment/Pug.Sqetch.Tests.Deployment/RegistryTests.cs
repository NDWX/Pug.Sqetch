using Pug.Sqetch.Deployment;
using Pug.Sqetch.DatabaseDriver;

namespace Pug.Sqetch.Tests.Deployment;

public class RegistryTests
{
	[Fact]
	public void DriversResolveCaseInsensitivelyAndUnknownNamesListTheKnownOnes()
	{
		DatabaseDriverRegistry registry = new ();

		Assert.Empty( registry.Names );

		registry.Register( "pg", () => new StubDriverFactory( "pg" ) );

		Assert.True( registry.TryCreate( "PG", out IDatabaseDriverFactory? factory ) );
		Assert.Equal( "pg", factory!.Name );

		UnknownDatabaseDriverException error =
			Assert.Throws<UnknownDatabaseDriverException>( () => registry.Create( "oracle" ) );

		Assert.Equal( "oracle", error.Name );
		Assert.Contains( "pg", error.Message );
	}

	private sealed class StubDriverFactory( string name ) : IDatabaseDriverFactory
	{
		public string Name => name;

		public DatabaseDriverParametersDefinition GetParametersDefinition() => new ( [], [] );

		public IDatabaseDriver Create( IDictionary<string, string> parameters, TimeSpan stepScriptTimeout )
			=> new FakeDatabaseDriver();
	}
}
