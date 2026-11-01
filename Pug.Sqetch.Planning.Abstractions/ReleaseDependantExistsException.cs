namespace Pug.Sqetch.Planning;

/// <summary>
/// Thrown when a release declares a dependency on a release that already has a dependant.
/// Releases form a single lineage, so each release may be depended on by at most one other
/// release.
/// </summary>
public class ReleaseDependantExistsException
	: Exception
{
	public string Dependency { get; }

	public string Dependant { get; }

	public ReleaseDependantExistsException( string dependency, string dependant )
	{
		Dependency = dependency;
		Dependant = dependant;
	}
}
