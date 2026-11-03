using Pug.Sqetch.DatabaseDriver;

namespace Pug.Sqetch.DatabaseDrivers.SqlServer;

public static class IDatabaseDriverRegistryExtensionMethods
{
	public static void RegisterSqlServerDriver( this IDatabaseDriverRegistry registry )
		=> registry.Register(
			SqlServerDatabaseDriverFactory.DriverName,
			() => new SqlServerDatabaseDriverFactory()
		);
}
