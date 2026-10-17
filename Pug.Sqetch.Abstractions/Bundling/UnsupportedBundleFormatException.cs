namespace Pug.Sqetch.Bundling;

/// <summary>
/// The bundle is well formed, but was written for a newer reader than this one: its layout is not
/// one this reader knows, or it is a layout format newer than the highest this reader understands.
/// Separate from the malformed-bundle failures because the remedy is a different one — upgrade the
/// tool, rather than fix the bundle.
/// </summary>
public class UnsupportedBundleFormatException
	: BundlingException
{
	public string Layout { get; }

	public int Format { get; }

	/// <summary>The highest format this reader understands of the layout; null when it does not know the layout at all.</summary>
	public int? Supported { get; }

	/// <summary>The bundle names a layout this reader does not know.</summary>
	public UnsupportedBundleFormatException( string layout, int format, IEnumerable<string> known )
		: base(
			$"the bundle uses layout '{layout}', which this sqetch-deploy does not know"
			+ $" (known: {Join( known )}); upgrade to a newer sqetch-deploy" )
	{
		Layout = layout;
		Format = format;
	}

	/// <summary>The bundle names a layout this reader knows, in a format newer than it reads.</summary>
	public UnsupportedBundleFormatException( string layout, int format, int supported )
		: base(
			$"the bundle uses format {format} of layout '{layout}'; this sqetch-deploy reads up to"
			+ $" format {supported}; upgrade to a newer sqetch-deploy" )
	{
		Layout = layout;
		Format = format;
		Supported = supported;
	}

	private static string Join( IEnumerable<string> known )
	{
		string[] names = known.ToArray();

		return names.Length == 0 ? "none" : string.Join( ", ", names );
	}
}
