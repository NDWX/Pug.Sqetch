using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Shared;

namespace Pug.Sqetch.Tests.Shared;

public class DatabaseDriverRegistryTests
{
	[Fact]
	public void DriversResolveCaseInsensitivelyAndUnknownNamesListTheKnownOnes()
	{
		DatabaseDriverRegistry registry = new();

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

		public DatabaseDriverParametersDefinition GetParametersDefinition() => new( [ ], [ ] );

		public IDatabaseDriver Create( IDictionary<string, string> parameters, TimeSpan stepScriptTimeout )
			=> throw new NotSupportedException( "the registry never creates a driver" );
	}
}
