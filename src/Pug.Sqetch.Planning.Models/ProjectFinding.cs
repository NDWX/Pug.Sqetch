namespace Pug.Sqetch.Planning.Models;

/// <summary>
/// One inconsistency 'sqetch project check' found: which check (<see cref="ProjectChecks"/>), where —
/// the release, plan and step it concerns, each "" when it does not apply — and what is wrong.
/// <paramref name="FixedByReindex"/> says whether rebuilding the index repairs it; everything else
/// needs a person's decision.
/// </summary>
public sealed record ProjectFinding(
	string Check,
	string Release,
	string Plan,
	string Step,
	string Message,
	bool FixedByReindex = false
);
