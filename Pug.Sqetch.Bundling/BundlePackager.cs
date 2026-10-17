namespace Pug.Sqetch.Bundling;

/// <summary>
/// Writes a bundle: the metadata entry first, then whatever the layout writes. The metadata is
/// written here, not by the layout, so that no layout can forget it, put it somewhere a reader
/// will not look, or disagree with the registry about its own name and format.
/// </summary>
public static class BundlePackager
{
	/// <summary>
	/// Writes <paramref name="bundle"/> to <paramref name="writer"/> through <paramref name="layout"/>,
	/// preceded by the metadata, which is returned. <paramref name="bundled"/> must be UTC.
	/// </summary>
	public static BundleMetadata Write(
		Bundle bundle, IBundleLayout layout, IBundleWriter writer, string bundler, string bundlerVersion,
		DateTime bundled )
	{
		ArgumentNullException.ThrowIfNull( bundle );
		ArgumentNullException.ThrowIfNull( layout );
		ArgumentNullException.ThrowIfNull( writer );

		BundleMetadata metadata = new ( layout.Format, layout.Name, bundler, bundlerVersion, bundled );

		metadata.Write( writer );
		layout.Write( bundle, writer );

		return metadata;
	}
}
