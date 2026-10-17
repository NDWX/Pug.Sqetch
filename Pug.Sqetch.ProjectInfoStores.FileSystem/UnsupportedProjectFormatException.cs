namespace Pug.Sqetch.ProjectInfoStores.FileSystem;

/// <summary>
/// The project's format is one this store does not know: written by a newer Sqetch, or not a
/// valid format number at all.
/// </summary>
public class UnsupportedProjectFormatException : ProjectStoreException
{
	public UnsupportedProjectFormatException( string root, int format, int supported )
		: base( format < 1
			? $"'{root}' declares project format {format}, which is not a valid format."
			: $"'{root}' is a format {format} Sqetch project; this version reads up to format {supported}. Upgrade Sqetch to work with it." )
	{
		Root = root;
		Format = format;
		Supported = supported;
	}

	public string Root { get; }

	public int Format { get; }

	public int Supported { get; }
}
