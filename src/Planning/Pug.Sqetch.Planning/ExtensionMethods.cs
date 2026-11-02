using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Planning;

internal static class ExtensionMethods
{
	public static void Validate( this ObjectDefinition definition )
	{
		if( string.IsNullOrWhiteSpace( definition.Name ) )
			throw new IncompleteDefinitionException();
	}
}