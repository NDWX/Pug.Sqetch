namespace Pug.Sqetch.Deployment.Bundling;

/// <summary>
/// One bundle being read. Entry paths use '/' separators. Dispose releases the bundle and
/// any temporary storage backing it, invalidating streams returned by <see cref="Open"/>.
/// </summary>
public interface IBundleReader
	: IDisposable
{
	IEnumerable<string> Entries { get; }

	bool Contains( string path );

	/// <summary>
	/// Opens the entry at <paramref name="path"/> for reading; the caller disposes the
	/// stream. Throws <see cref="BundlingException"/> when the entry does not exist.
	/// </summary>
	Stream Open( string path );
}
