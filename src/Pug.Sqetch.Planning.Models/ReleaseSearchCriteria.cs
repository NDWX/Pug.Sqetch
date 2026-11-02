namespace Pug.Sqetch.Planning.Models;

/// <summary>
/// Filter criteria for release listings. With no criteria set, only unfinalized releases match.
/// <see cref="Finalized"/> (implied by <see cref="FinalizeTimestamp"/> or
/// <see cref="FinalizeUser"/>) selects finalized releases instead. User criteria match the
/// e-mail address of the user who registered or finalized the release.
/// </summary>
public record ReleaseSearchCriteria(
	string Prefix = "",
	bool Finalized = false,
	Range<DateTime>? CreateTimestamp = null,
	Range<DateTime>? FinalizeTimestamp = null,
	string? CreateUser = null,
	string? FinalizeUser = null
);