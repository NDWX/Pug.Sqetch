namespace Pug.Sqetch.Planning.Models;

/// <summary>
/// The codes of <see cref="ProjectFinding.Check"/>: stable kebab-case names, since CI and '-o json'
/// consumers match on them. The first group is the file store's (the project on disk), the second the
/// engine's (the rules a project's content must follow).
/// </summary>
public static class ProjectChecks
{
	// the store: the project on disk

	/// <summary>The plan index file is absent.</summary>
	public const string IndexMissing = "index-missing";

	/// <summary>The plan index file cannot be parsed.</summary>
	public const string IndexUnreadable = "index-unreadable";

	/// <summary>
	/// The plan index disagrees with the plan folders: an entry with no folder, a folder with no entry,
	/// or a release or dependencies differing from the plan's plan.json.
	/// </summary>
	public const string IndexDrift = "index-drift";

	/// <summary>A plan.json whose 'release' differs from the release folder the plan is in.</summary>
	public const string PlanReleaseMismatch = "plan-release-mismatch";

	/// <summary>A plan.json naming a plan other than its folder.</summary>
	public const string PlanNameMismatch = "plan-name-mismatch";

	/// <summary>A step.json naming a step other than its folder.</summary>
	public const string StepNameMismatch = "step-name-mismatch";

	/// <summary>A step depending on a step its plan does not have.</summary>
	public const string StepDependencyUnknown = "step-dependency-unknown";

	/// <summary>A project, release, plan or step name the naming rules reject.</summary>
	public const string InvalidName = "invalid-name";

	/// <summary>
	/// The same plan in more than one folder (names compared case-insensitively, as the index does):
	/// what a mangled merge or a copied folder leaves. Reindex would keep one copy at random.
	/// </summary>
	public const string PlanDuplicate = "plan-duplicate";

	/// <summary>
	/// The same release in more than one folder, or two releases whose names differ only in case,
	/// which a case-insensitive file system cannot hold apart.
	/// </summary>
	public const string ReleaseDuplicate = "release-duplicate";

	/// <summary>A release.json naming a release other than its folder.</summary>
	public const string ReleaseNameMismatch = "release-name-mismatch";

	/// <summary>A plan, step or release document that cannot be parsed, such as one holding merge-conflict markers.</summary>
	public const string DocumentUnreadable = "document-unreadable";

	/// <summary>
	/// A plan folder without plan.json, or a release folder without release.json: invisible to every
	/// command, along with whatever is inside it.
	/// </summary>
	public const string OrphanFolder = "orphan-folder";

	// the engine: the rules

	/// <summary>A released plan whose dependency is unreleased or in a later release.</summary>
	public const string ReleasedPlanMissingDependency = "released-plan-missing-dependency";

	/// <summary>A plan depending on a plan the project does not have.</summary>
	public const string PlanDependencyUnknown = "plan-dependency-unknown";

	/// <summary>Plans that depend on each other in a cycle.</summary>
	public const string PlanDependencyCycle = "plan-dependency-cycle";

	/// <summary>
	/// The releases do not form one line: a release with more than one dependant, a missing
	/// predecessor, more than one release starting the lineage, releases depending on each other in a
	/// cycle, or an unfinalized release before a finalized one.
	/// </summary>
	public const string ReleaseLineage = "release-lineage";

	/// <summary>A step whose deploy, verify or rollback script is missing.</summary>
	public const string StepScriptsMissing = "step-scripts-missing";

	/// <summary>A step requiring more than one step: allowed before 0.2, refused for new writes since.</summary>
	public const string StepMultipleDependencies = "step-multiple-dependencies";

	/// <summary>A required journaling slot without SQL.</summary>
	public const string JournalingUnset = "journaling-unset";

	// the command

	/// <summary>The rule checks were not run, because the index they read through cannot be trusted.</summary>
	public const string ChecksSkipped = "checks-skipped";

	/// <summary>
	/// Whether a finding makes what the rule checks read untrustworthy — the plan index, a plan's
	/// release, which copy of a plan or release is meant, or a document itself — so they would fail
	/// or report what is not there, and must not run until it is resolved.
	/// </summary>
	public static bool IsBlocking( string check )
		=> check is IndexMissing or IndexUnreadable or IndexDrift or PlanReleaseMismatch or PlanNameMismatch
			or PlanDuplicate or ReleaseDuplicate or ReleaseNameMismatch or DocumentUnreadable;
}
