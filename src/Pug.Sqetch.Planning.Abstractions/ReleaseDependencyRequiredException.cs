namespace Pug.Sqetch.Planning;

/// <summary>
/// Thrown when a release is created without declaring the release it depends on; only the
/// project's first release may omit its dependency.
/// </summary>
public class ReleaseDependencyRequiredException
	: Exception
{
}
