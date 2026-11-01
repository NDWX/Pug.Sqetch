namespace Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

public class ProjectStoreException : Exception
{
	public ProjectStoreException( string message, Exception? innerException = null )
		: base( message, innerException )
	{
	}
}
