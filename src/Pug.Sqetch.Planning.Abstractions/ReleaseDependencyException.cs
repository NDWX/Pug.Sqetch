using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Planning;

public class ReleaseDependencyException
	: Exception
{
	public IEnumerable<ProjectRelease> ReleaseDependencies { get; }

	public ReleaseDependencyException( string message, IEnumerable<ProjectRelease> releaseDependencies )
		: base( message )
	{
		ReleaseDependencies = releaseDependencies;
	}
}