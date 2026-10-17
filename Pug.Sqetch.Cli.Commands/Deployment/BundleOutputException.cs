namespace Pug.Sqetch.Cli.Commands.Deployment;

/// <summary>
/// The bundle's output could not be created or written: the target already exists, its directory
/// cannot be made, the disk is full. Its own type so the exit code can say "the project was read
/// and bundled fine, only the output is wrong" without mapping every <see cref="IOException"/>
/// the process might ever raise to the same code.
/// </summary>
internal sealed class BundleOutputException
	: Exception
{
	public string Path { get; }

	public BundleOutputException( string path, Exception cause )
		: base( $"cannot write bundle output '{path}': {cause.Message}", cause )
	{
		Path = path;
	}
}
