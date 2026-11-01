namespace Pug.Sqetch.Deployment.Bundling;

/// <summary>
/// A pluggable kind of bundle output (an archive file or a directory tree). Custom types
/// plug in through <see cref="BundleTypeRegistry"/>.
/// </summary>
public interface IBundleType
{
	string Name { get; }

	/// <summary>Default output name for a bundle, e.g. the base name plus an extension.</summary>
	string DefaultOutputName( string baseName );

	/// <summary>
	/// Creates the bundle output at <paramref name="outputPath"/>; refuses an existing
	/// target with a <see cref="BundlingException"/>.
	/// </summary>
	IBundleWriter Create( string outputPath );

	/// <summary>
	/// Opens an existing bundle at <paramref name="path"/> for reading; throws
	/// <see cref="BundlingException"/> when the bundle is missing or cannot be read.
	/// </summary>
	IBundleReader Open( string path );
}
