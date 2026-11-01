namespace Pug.Sqetch.DatabaseDriver;

public class UnknownDatabaseDriverException
	: DatabaseDriverException
{
	public string Name { get; }

	public IReadOnlyList<string> Known { get; }

	public UnknownDatabaseDriverException( string name, IEnumerable<string> known )
		: base( $"Unknown database driver '{name}'; known drivers: {string.Join( ", ", known )}." )
	{
		Name = name;
		Known = known.ToList();
	}
}
