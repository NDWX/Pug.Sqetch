namespace Pug.Sqetch.Deployment.Bundling;

public class UnknownBundleLayoutException
	: BundlingException
{
	public string Name { get; }

	public UnknownBundleLayoutException( string name, IEnumerable<string> known )
		: base( $"Unknown bundle layout '{name}'; known layouts: {string.Join( ", ", known )}." )
	{
		Name = name;
	}
}