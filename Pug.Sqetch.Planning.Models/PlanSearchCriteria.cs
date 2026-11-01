namespace Pug.Sqetch.Planning.Models;

/// <summary>
/// Filter criteria for plan listings. With no criteria set, only unreleased plans match.
/// <see cref="Release"/> selects plans of one specific release; <see cref="Released"/> (implied
/// by <see cref="Release"/> or <see cref="ReleaseFinalizeTimestamp"/>) selects plans belonging
/// to any release, narrowed to releases finalized within <see cref="ReleaseFinalizeTimestamp"/>
/// when specified. <see cref="CreateUser"/> matches the e-mail address of the user who
/// registered the plan.
/// </summary>
public record PlanSearchCriteria(
	string? Release = null,
	bool Released = false,
	Range<DateTime>? ReleaseFinalizeTimestamp = null,
	string? CreateUser = null );
