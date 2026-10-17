using Pug.Sqetch.Models;

namespace Pug.Sqetch.Bundling;

/// <summary>
/// Assembles the deployable content of a project into a <see cref="Bundle"/>, verifying
/// that no included plan is missing a step script and that the project's journaling SQL is
/// complete.
/// </summary>
public class BundleBuilder
{
	private readonly IReadOnlyProject _project;

	public BundleBuilder( IReadOnlyProject project )
	{
		ArgumentNullException.ThrowIfNull( project );

		_project = project;
	}

	/// <summary>
	/// Assembles the bundle in deployment-chronological order. When <paramref name="since"/>
	/// is given, only releases strictly after it in the chain are included — a continuation
	/// bundle. Throws <see cref="EmptyBundleException"/> when no plans match
	/// <paramref name="selection"/>, <see cref="UnknownReleaseException"/> when
	/// <paramref name="since"/> does not name a release in the chain,
	/// <see cref="MissingStepScriptsException"/> when an included plan is missing a step
	/// script, and <see cref="MissingJournalingStatementsException"/> when the project has not set
	/// every journaling statement.
	/// </summary>
	public Bundle Assemble( BundleSelection selection, string? since = null )
	{
		List<ProjectRelease> releases = SelectReleases( selection, since );
		List<BundlePlan> plans = new ();

		foreach( ProjectRelease release in releases )
			plans.AddRange(
				_project.GetPlans( new PlanSearchCriteria( Release: release.Definition.Name, Released: true ) )
					.Select( ToBundlePlan ) );

		// unreleased plans deploy last: released plans can never depend on them
		if( selection == BundleSelection.Test )
			plans.AddRange( _project.GetPlans( new PlanSearchCriteria() ).Select( ToBundlePlan ) );

		if( plans.Count == 0 )
			throw new EmptyBundleException();

		foreach( BundlePlan plan in plans )
			_project.VerifyStepScripts( plan.Name );

		// the journal is deployed through the project's own SQL, so a bundle without it could
		// never be deployed; refused here rather than at deploy time, where the operator has no
		// way to fix it
		_project.VerifyJournalingStatements();

		return new Bundle(
			_project.GetDefinition(),
			selection,
			new JournalingStatements(
				JournalingSlots.All.ToDictionary(
					slot => slot, slot => _project.GetJournalingStatement( slot ) ) ),
			releases.Select( x => new BundleRelease(
				x.Definition.Name, x.Definition.Description, x.Definition.Dependency ?? "", x.Finalized is not null ) ).ToList(),
			plans );
	}

	private List<ProjectRelease> SelectReleases( BundleSelection selection, string? since )
	{
		List<ProjectRelease> releases =
			_project.GetReleases( new ReleaseSearchCriteria( Finalized: true ) ).ToList();

		// finalized releases are always the prefix of the single release lineage, so open
		// releases follow them in chain order; 'since' needs the full chain too, so a
		// '--since' naming an open release is diagnosable rather than reported as unknown
		if( selection == BundleSelection.Test || since is not null )
			releases.AddRange( _project.GetReleases( new ReleaseSearchCriteria(Finalized: false) ) );

		if( since is not null )
		{
			int index = releases.FindIndex(
				x => string.Equals( x.Definition.Name, since, StringComparison.OrdinalIgnoreCase ) );

			if( index < 0 )
				throw new UnknownReleaseException( since );

			releases = releases.Skip( index + 1 ).ToList();
		}

		return selection == BundleSelection.Test
			? releases
			: releases.Where( x => x.Finalized is not null ).ToList();
	}

	private BundlePlan ToBundlePlan( ProjectPlan plan )
	{
		string name = plan.Definition.Name;

		return new BundlePlan(
			name,
			plan.Definition.Description,
			plan.Release,
			( ( plan.Definition as PlanDefinition )?.Dependencies ?? [] ).ToList(),
			_project.GetSteps( name ).Select( x => ToBundleStep( name, x ) ).ToList() );
	}

	private BundleStep ToBundleStep( string plan, ProjectElement step )
		=> new (
			step.Definition.Name,
			step.Definition.Description,
			( ( step.Definition as StepDefinition )?.Dependencies ?? [] ).ToList(),
			() => _project.GetStepScripts( plan, step.Definition.Name ) );
}
