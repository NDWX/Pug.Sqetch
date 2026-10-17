using Pug.Sqetch.Models;

namespace Pug.Sqetch;

public interface IReadOnlyProject : IDisposable
{
	/// <summary>The project's name, description and engine, as set when it was initialized.</summary>
	ProjectDefinition GetDefinition();

	IEnumerable<ProjectPlan> GetDependants( string plan );
	IEnumerable<ProjectPlan> GetDependencies( string plan );

	/// <summary>
	/// Steps of <paramref name="plan"/> in dependency-chronological order: a step always
	/// follows the steps it depends on, ties broken by registration time.
	/// </summary>
	IEnumerable<ProjectElement> GetSteps( string plan );

	/// <summary>
	/// Opens the scripts of <paramref name="step"/>; the caller owns and must dispose the
	/// returned streams. Throws <see cref="MissingStepScriptsException"/> when any of the
	/// step's script files is missing.
	/// </summary>
	StepScripts GetStepScripts( string plan, string step );

	/// <summary>
	/// Confirms every step of <paramref name="plan"/> has all of its script files; throws
	/// <see cref="MissingStepScriptsException"/> for the first step with a missing script.
	/// </summary>
	void VerifyStepScripts( string plan );

	/// <summary>
	/// Releases matching <paramref name="criteria"/> in dependency-chronological order: a
	/// release always follows the release it depends on, ties broken by registration time.
	/// </summary>
	IEnumerable<ProjectRelease> GetReleases( ReleaseSearchCriteria criteria );

	IEnumerable<ProjectRelease> GetReleaseDependants( string release );

	/// <summary>
	/// Plans matching <paramref name="criteria"/> in dependency-chronological order:
	/// released plans are ordered by release chronology first (a plan in a later release
	/// always follows every plan of an earlier release), then by plan dependencies within
	/// each release, ties broken by registration time.
	/// </summary>
	IEnumerable<ProjectPlan> GetPlans( PlanSearchCriteria criteria );

	/// <summary>
	/// The project's journaling SQL for <paramref name="slot"/>, or <c>null</c> when it has not
	/// been set. On this interface, not <see cref="IProject"/>, because <c>BundleBuilder</c> holds
	/// an <see cref="IReadOnlyProject"/> and reads it from there.
	/// </summary>
	string? GetJournalingStatement( JournalingSlot slot );

	/// <summary>
	/// Confirms every <see cref="JournalingSlot"/> has a statement; throws
	/// <see cref="MissingJournalingStatementsException"/> naming every missing slot, unlike
	/// <see cref="VerifyStepScripts"/> which stops at the first failing step.
	/// </summary>
	void VerifyJournalingStatements();
}
