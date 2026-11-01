namespace Pug.Sqetch.Planning;

public class UnknownReleaseException
	: Exception
{
	public string ReleaseName { get; }

	public UnknownReleaseException(string releaseName)
	{
		ReleaseName = releaseName;
	}
}