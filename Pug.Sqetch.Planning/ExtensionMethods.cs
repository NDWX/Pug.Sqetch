using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Planning;

internal static class ExtensionMethods
{
	public static void Coalesce(
		this IDictionary<string, (ProjectPlan, ICollection<string>)> existing,
		IDictionary<string, (ProjectPlan, ICollection<string>)> dependencies, string dependant
	)
	{
		foreach(  KeyValuePair<string, (ProjectPlan, ICollection<string>)> dependency  in dependencies )
		{
			ICollection<string>? dependants = null;

			if( existing.TryGetValue( dependency.Key, out (ProjectPlan, ICollection<string>) details ) )
			{
				dependants = details.Item2;
				dependants.Add(dependant);
			}
			else
			{
				dependants = new List<string>() { dependant };
				existing.Add(dependency.Key, (dependency.Value.Item1, dependants));
			}
		}
	}

	public static void Validate(this ObjectDefinition definition)
	{
		if( string.IsNullOrWhiteSpace( definition.Name ) )
			throw new IncompleteDefinitionException();
	}
}