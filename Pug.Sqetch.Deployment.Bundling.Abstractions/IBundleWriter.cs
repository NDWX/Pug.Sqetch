namespace Pug.Sqetch.Deployment.Bundling;

/// <summary>
/// One bundle being written. Entry paths use '/' separators; content streams are read
/// fully and remain owned by the caller. Dispose finishes the bundle output.
/// </summary>
public interface IBundleWriter
	: IDisposable
{
	void Add( string path, Stream content );
}
