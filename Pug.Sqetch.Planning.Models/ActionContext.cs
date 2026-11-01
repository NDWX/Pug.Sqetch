namespace Pug.Sqetch.Planning.Models;

public record ActionContext<TSubject>( TSubject Subject, DateTime Timestamp );

public record ActionContext( UserInfo Subject, DateTime Timestamp )
	: ActionContext<UserInfo>( Subject, Timestamp );