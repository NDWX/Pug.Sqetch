namespace Pug.Sqetch.Deployment.Bundling;

/// <summary>
/// Thrown when the bundle a caller names is not there at all. Separate from the malformed-bundle
/// failures because the remedy is a different one — the pipeline produced nothing, or named the
/// wrong path, rather than produced something unusable.
/// </summary>
public class MissingBundleException
	: BundlingException
{
	public string Path { get; }

	public MissingBundleException( string path )
		: base( $"Bundle '{path}' does not exist." )
	{
		Path = path;
	}
}