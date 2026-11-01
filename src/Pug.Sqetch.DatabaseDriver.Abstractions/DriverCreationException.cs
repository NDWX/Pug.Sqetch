namespace Pug.Sqetch.DatabaseDriver;

/// <summary>The selected database driver refused the provided parameters or failed to initialize.</summary>
public class DriverCreationException
	: DatabaseDriverException
{
	public string Driver { get; }

	public DriverCreationException( string driver, Exception innerException )
		: base( $"Failed to create database driver '{driver}': {innerException.Message}", innerException )
	{
		Driver = driver;
	}
}
