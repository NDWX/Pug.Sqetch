namespace Pug.Sqetch.Planning.Models;

public record ProjectRelease( ReleaseDefinition Definition, ActionContext? Finalized, ActionContext Registration ) 
	: ProjectElement<ReleaseDefinition>( Definition, Registration );