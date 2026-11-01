using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStore;

namespace Pug.Sqetch.Planning;

/// <summary>
/// Determines the ordering relationship between two releases by walking the release
/// dependency chain (each release names at most one predecessor in its definition).
/// </summary>
public class ReleaseChainDependencyDeterminator : IReleaseDependencyDeterminator
{
	private readonly IProjectInfoStore _infoStore;

	public ReleaseChainDependencyDeterminator( IProjectInfoStore infoStore )
	{
		_infoStore = infoStore ?? throw new ArgumentNullException( nameof(infoStore) );
	}

	public DependencyRelationship DetermineDependencyRelationship( string first, string second )
	{
		if( string.Equals( first, second, StringComparison.OrdinalIgnoreCase ) )
			return DependencyRelationship.None;

		if( ChainReaches( first, second ) )
			return DependencyRelationship.Dependant;

		if( ChainReaches( second, first ) )
			return DependencyRelationship.Dependency;

		return DependencyRelationship.None;
	}

	private bool ChainReaches( string from, string target )
	{
		HashSet<string> visited = new ( StringComparer.OrdinalIgnoreCase );

		string current = from;

		while( visited.Add( current ) )
		{
			ProjectRelease? release = _infoStore.GetRelease( current );

			if( release is null || string.IsNullOrEmpty( release.Definition.Dependency ) )
				return false;

			if( string.Equals( release.Definition.Dependency, target, StringComparison.OrdinalIgnoreCase ) )
				return true;

			current = release.Definition.Dependency;
		}

		return false;
	}
}
