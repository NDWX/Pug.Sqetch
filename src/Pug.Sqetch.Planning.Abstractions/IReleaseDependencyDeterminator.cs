using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Planning;

public interface IReleaseDependencyDeterminator
{
	DependencyRelationship DetermineDependencyRelationship(string first,  string second);
}