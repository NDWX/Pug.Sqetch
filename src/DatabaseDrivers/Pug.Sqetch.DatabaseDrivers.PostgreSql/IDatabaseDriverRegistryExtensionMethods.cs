using Pug.Sqetch.DatabaseDriver;

namespace Pug.Sqetch.DatabaseDrivers.PostgreSql;

public static class IDatabaseDriverRegistryExtensionMethods
{
	public static void RegisterPostgreSqlDriver( this IDatabaseDriverRegistry registry )
		=> registry.Register(
			PostgreSqlDatabaseDriverFactory.DriverName, () => new PostgreSqlDatabaseDriverFactory() );
}
