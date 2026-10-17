using Pug.Sqetch.Models;

namespace Pug.Sqetch.ProjectInfoStores.FileSystem;

// On-disk JSON shapes. Property declaration order is the serialized order, so keep it
// stable — it defines the diff-friendly file format, not just an implementation detail.

internal sealed record UserDocument( string Name, string EmailAddress )
{
	public static UserDocument From( UserInfo user ) => new ( user.Name, user.EmailAddress );

	public UserInfo ToModel() => new ( Name, EmailAddress );
}

internal sealed record ActionDocument( UserDocument Subject, DateTime Timestamp )
{
	public static ActionDocument From( ActionContext context ) => new ( UserDocument.From( context.Subject ), context.Timestamp );

	public static ActionDocument? FromNullable( ActionContext? context ) => context is null ? null : From( context );

	public ActionContext ToModel() => new ( Subject.ToModel(), Timestamp );
}

/// <summary>
/// <paramref name="Format"/> is the on-disk format version, first so it is the first thing a
/// reader meets; <c>null</c> in a project written before the field existed, which is format 1.
/// </summary>
internal sealed record ProjectDocument(
	int? Format,
	string Name,
	string Description,
	string Engine,
	ActionDocument Registration,
	ShardingConfiguration? ReleaseSharding );

internal sealed record PlanDocument(
	string Name,
	string Description,
	string[] Dependencies,
	string Release,
	ActionDocument Registration,
	ActionDocument? ReleaseAssignment )
{
	public ProjectPlan ToModel()
		=> new ( new PlanDefinition( Name, Description, Dependencies ), Release, Registration.ToModel() );
}

internal sealed record StepDocument(
	string Name,
	string Description,
	string[] Dependencies,
	ActionDocument Registration )
{
	public ProjectElement ToModel( string plan )
		=> new ( new StepDefinition( plan, Name, Description, Dependencies ), Registration.ToModel() );
}

internal sealed record ReleaseDocument(
	string Name,
	string Description,
	string Dependency,
	ActionDocument Registration,
	ActionDocument? Finalized )
{
	public ProjectRelease ToModel()
		=> new ( new ReleaseDefinition( Name, Description, Dependency ), Finalized?.ToModel(), Registration.ToModel() );
}
