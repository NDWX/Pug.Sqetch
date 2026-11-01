using Pug.Sqetch.DatabaseDriver;

namespace Pug.Sqetch.DatabaseDrivers.Sqlite;

public static class IDatabaseDriverRegistryExtensionMethods
{
	public static void RegisterSqliteDriver( this IDatabaseDriverRegistry registry )
		=> registry.Register( SqliteDatabaseDriverFactory.DriverName, () => new SqliteDatabaseDriverFactory() );
}
