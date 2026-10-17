using Pug.Sqetch.Bundling;
using Pug.Sqetch.Models;

namespace Pug.Sqetch.Deployment;

/// <summary>
/// Everything a deployment needs out of a bundle, read and checked before the database is touched:
/// the manifest and the project's journaling SQL, plus the metadata and the layout it resolved to —
/// the layout the rest of the deployment must read the bundle's scripts through.
/// <see cref="Journaling"/> is data, not a strategy — the deployment engine is handed it rather than
/// reading the bundle itself, so an unreadable statement is refused before a driver is ever created.
/// </summary>
public sealed record ValidatedBundle(
	BundleManifest Manifest,
	JournalingStatements Journaling,
	BundleMetadata Metadata,
	IBundleLayout Layout );
