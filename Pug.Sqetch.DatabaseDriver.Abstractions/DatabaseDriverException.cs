namespace Pug.Sqetch.DatabaseDriver;

/// <summary>
/// Base class for database driver failures: selecting a driver, creating it, or reaching the database
/// through it. Separate from deployment failures because a driver is a contract of its own — whoever
/// implements one, or hosts one outside a deployment, should not have to depend on the deployment's
/// exceptions to report what went wrong.
/// </summary>
public class DatabaseDriverException
	: Exception
{
	public DatabaseDriverException( string message )
		: base( message )
	{
	}

	public DatabaseDriverException( string message, Exception innerException )
		: base( message, innerException )
	{
	}
}
