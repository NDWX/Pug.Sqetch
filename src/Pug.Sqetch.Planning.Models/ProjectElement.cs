namespace Pug.Sqetch.Planning.Models;

public record ProjectElement<TDefinition>( TDefinition Definition, ActionContext Registration )
	where TDefinition : ObjectDefinition;

public record ProjectElement(ObjectDefinition Definition, ActionContext Registration) 
	: ProjectElement<ObjectDefinition>(Definition, Registration);