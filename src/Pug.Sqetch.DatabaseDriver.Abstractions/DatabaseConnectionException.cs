namespace Pug.Sqetch.DatabaseDriver;

/// <summary>
/// Thrown when the database cannot be reached: the connection a driver opens on first use failed.
/// Kept apart from <see cref="DriverCreationException"/> because a driver is created without
/// connecting — the parameters were accepted and the provider was happy to be configured, and what
/// failed is reaching the server. That is the most ordinary pipeline failure there is, and the one a
/// caller is most likely to want to retry rather than investigate.
/// </summary>
public class DatabaseConnectionException
	: DatabaseDriverException
{
	public DatabaseConnectionException( Exception innerException )
		: base( $"could not connect to the database: {innerException.Message}", innerException )
	{
	}
}
