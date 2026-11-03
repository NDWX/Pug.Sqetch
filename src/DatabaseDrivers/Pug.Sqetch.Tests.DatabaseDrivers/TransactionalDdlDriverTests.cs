using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.DatabaseDrivers.PostgreSql;
using Pug.Sqetch.DatabaseDrivers.Sqlite;
using Pug.Sqetch.DatabaseDrivers.SqlServer;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// The engines that run DDL inside the transaction must not claim otherwise: the flag makes a failed
/// run count as a change and compensates steps a transaction rollback already took back.
/// </summary>
public class TransactionalDdlDriverTests
{
	[Theory]
	[InlineData( "sqlite" )]
	[InlineData( "postgres" )]
	[InlineData( "sqlserver" )]
	public void TransactionalEnginesDoNotClaimImplicitDdlCommits( string name )
	{
		(IDatabaseDriverFactory factory, Dictionary<string, string> parameters) = name switch
		{
			"sqlite" => ( (IDatabaseDriverFactory)new SqliteDatabaseDriverFactory(),
				new Dictionary<string, string> { ["file"] = Path.Combine( Path.GetTempPath(), $"{Guid.NewGuid():N}.db" ) } ),
			"postgres" => ( new PostgreSqlDatabaseDriverFactory(), new Dictionary<string, string> { ["host"] = "a", ["database"] = "b" } ),
			_ => ( new SqlServerDatabaseDriverFactory(), new Dictionary<string, string> { ["host"] = "a", ["database"] = "b" } )
		};

		using IDatabaseDriver driver = factory.Create( parameters, TimeSpan.FromSeconds( 30 ) );

		Assert.False( driver.CommitsDdlImplicitly );
	}
}
