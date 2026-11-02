namespace Pug.Sqetch.Planning.Models;

public record ProjectDefinition( string Name, string Description, string Engine )
	: ObjectDefinition( Name, Description );