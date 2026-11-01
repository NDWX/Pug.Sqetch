namespace Pug.Sqetch.Planning.Models;

public record PlanDefinition( string Name, string Description, ICollection<string> Dependencies ) 
	: ObjectDefinition( Name, Description );