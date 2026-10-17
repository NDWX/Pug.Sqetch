namespace Pug.Sqetch.Bundling;

/// <summary>
/// The bundle's <c>META-DATA</c> entry is missing, unreadable or invalid — or a
/// <see cref="BundleMetadata"/> that could not be written back out as such. The message names what
/// is wrong.
/// </summary>
public class InvalidBundleMetadataException
	: BundlingException
{
	public InvalidBundleMetadataException( string message )
		: base( message )
	{
	}
}
