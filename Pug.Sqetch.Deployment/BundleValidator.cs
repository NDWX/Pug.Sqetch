using Pug.Sqetch.Bundling;
using Pug.Sqetch.Models;

namespace Pug.Sqetch.Deployment;

public static class BundleValidator
{
	/// <summary>
	/// Reads the bundle's metadata first, resolves the layout it names from <paramref name="layouts"/>
	/// and checks that this reader understands the format it declares; only then reads the manifest
	/// and the project's journaling SQL, and confirms every step listed in the manifest has all three
	/// scripts in the bundle. Throws <see cref="InvalidBundleMetadataException"/> for missing or
	/// invalid metadata, <see cref="UnsupportedBundleFormatException"/> for a layout this reader does
	/// not know or a format newer than it reads, <see cref="InvalidBundleException"/> naming every
	/// missing entry, <see cref="InvalidBundleManifestException"/> for a missing or invalid manifest, or
	/// <see cref="BundlingException"/> when the journaling statements are missing or invalid.
	/// Metadata comes before anything the layout owns because the layout is not known until it is read,
	/// and a bundle of a newer format must be refused for that rather than misread as invalid.
	/// Journaling is read here, before any driver exists, so a bundle that could never be journaled
	/// is refused without touching the database.
	/// </summary>
	public static ValidatedBundle Validate( IBundleReader reader, IBundleLayoutRegistry layouts )
	{
		BundleMetadata metadata = BundleMetadata.Read( reader );

		if( !layouts.TryCreate( metadata.Layout, out IBundleLayout? layout ) )
			throw new UnsupportedBundleFormatException( metadata.Layout, metadata.Format, layouts.Names );

		if( metadata.Format > layout.Format )
			throw new UnsupportedBundleFormatException( metadata.Layout, metadata.Format, layout.Format );

		BundleManifest manifest = layout.ReadManifest( reader );
		JournalingStatements journaling = layout.ReadJournalingStatements( reader );

		List<string> missing = new ();

		foreach( BundleManifestPlan plan in manifest.Plans )
			foreach( BundleManifestStep step in plan.Steps )
				foreach( StepScriptKind kind in Enum.GetValues<StepScriptKind>() )
				{
					string path = layout.ScriptPath( plan.Name, step.Name, kind );

					if( !reader.Contains( path ) )
						missing.Add( path );
				}

		if( missing.Count > 0 )
			throw new InvalidBundleException(
				$"Bundle is missing entries listed in its manifest: {string.Join( ", ", missing )}." );

		return new ValidatedBundle( manifest, journaling, metadata, layout );
	}
}
