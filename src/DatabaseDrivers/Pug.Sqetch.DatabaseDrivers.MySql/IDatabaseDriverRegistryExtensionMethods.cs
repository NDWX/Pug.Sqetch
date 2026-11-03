using Pug.Sqetch.DatabaseDriver;

namespace Pug.Sqetch.DatabaseDrivers.MySql;

public static class IDatabaseDriverRegistryExtensionMethods
{
	public static void RegisterMySqlDriver( this IDatabaseDriverRegistry registry )
		=> registry.Register(
			MySqlDatabaseDriverFactory.DriverName,
			() => new MySqlDatabaseDriverFactory()
		);
}
