 namespace Pug.Sqetch.Planning.Models;

public record ProjectPlan( ObjectDefinition Definition, string Release, ActionContext Registration ) 
	: ProjectElement( Definition, Registration );