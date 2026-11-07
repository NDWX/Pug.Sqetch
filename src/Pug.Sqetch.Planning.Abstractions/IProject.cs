using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Planning;

public interface IProject
	: IDisposable, IReadOnlyProject
{
	void Add( PlanDefinition plan, string? release = null );

	Task AddAsync( PlanDefinition plan, string? release = null );

	void DeletePlan( string name );

	Task DeletePlanAsync( string name );

	StepScriptKeys Add( StepDefinition step, string plan );

	void Delete( string plan, string step );

	/// <summary>
	/// Changes a plan's description and/or dependencies; <c>null</c> leaves either unchanged. A
	/// dependency must exist and not be the plan itself; for a released plan it must be in the
	/// plan's release or an earlier one (<see cref="MissingPlanDependenciesException"/>), and no
	/// dependency may lead back to the plan (<see cref="PlanDependencyCycleException"/>). Refused for
	/// a plan of a finalized release. Everything is checked before the one write.
	/// </summary>
	void UpdatePlan( string plan, string? description, ICollection<string>? dependencies );

	/// <summary>Changes the description of an open release.</summary>
	void UpdateRelease( string release, string description );

	/// <summary>Changes a step's description.</summary>
	void UpdateStep( string plan, string step, string description );

	/// <summary>
	/// Renames a step, its scripts included; the steps depending on it follow. Refused when the new
	/// name is taken or differs only in case.
	/// </summary>
	void RenameStep( string plan, string step, string newName );

	/// <summary>
	/// Moves <paramref name="step"/> right before or after <paramref name="anchor"/> by changing
	/// dependencies, as in a linked list: the steps requiring <paramref name="step"/> take its
	/// dependency, then <paramref name="step"/> requires <paramref name="anchor"/> and the steps that
	/// required <paramref name="anchor"/> require <paramref name="step"/> (after), or
	/// <paramref name="step"/> takes <paramref name="anchor"/>'s dependency and
	/// <paramref name="anchor"/> requires <paramref name="step"/> (before). Each write leaves the
	/// plan acyclic, so a move cut short leaves a valid plan and running it again finishes it.
	/// Returns the steps whose dependency changed, in the order they were written.
	/// </summary>
	IReadOnlyList<StepDependencyChange> MoveStep( string plan, string step, StepPlacement placement, string anchor );

	void CreateRelease( ReleaseDefinition definition, IEnumerable<string> plans );

	void CreateRelease( ReleaseDefinition definition, bool includeUnreleasedPlans = false );

	void AddPlanToRelease( string release, string plan, bool includeDependencies );

	/// <summary>
	/// Puts <paramref name="plan"/> in <paramref name="release"/>, from another release or from
	/// unreleased, checking everything before changing anything: a refused move leaves the plan
	/// where it was. Refuses with <see cref="MissingPlanDependenciesException"/> when a dependency
	/// would not be in the release or an earlier one (unreleased ones come along with
	/// <paramref name="includeDependencies"/>), and with <see cref="StrandedPlanDependantsException"/>
	/// when a dependant would be left in an earlier release. Does nothing when the plan is already
	/// there.
	/// </summary>
	void MovePlanToRelease( string plan, string release, bool includeDependencies );

	void RemovePlanFromRelease( string release, string plan, bool includeDependants = false );

	void DeleteRelease( string release );

	void FinalizeRelease( string release );

	/// <summary>
	/// Sets the project's journaling SQL for <paramref name="slot"/>; throws
	/// <see cref="ArgumentException"/> when <paramref name="statement"/> does not parse into at
	/// least one statement, or (for a query slot) parses into more than one. No finalized-release
	/// guard: journaling SQL is project-level, not tied to any one release.
	/// </summary>
	void SetJournalingStatement( JournalingSlot slot, string statement );
}