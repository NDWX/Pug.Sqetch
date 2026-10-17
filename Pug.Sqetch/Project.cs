using System.Transactions;
using Pug.Sqetch.Models;
using Pug.Sqetch.ProjectInfoStore;

namespace Pug.Sqetch;

/*
public interface IPersistenceProvider
{
	IObject<ProjectInfo> Create(ProjectInfo project);

	Task<IObject<ProjectInfo>> CreateAsync(ProjectInfo project);

	IObject<ProjectInfo>? OpenProjectInfo();

	Task<IObject<ProjectInfo>?> OpenProjectInfoAsync();

	IObject<PlanInfo?>? OpenPlanInfo(string name);

	Task<IObject<PlanInfo>?> OpenPlanInfoAsync(string name);

	IObject<PlanInfo> Create(PlanInfo plan);

	Task<IObject<PlanInfo>> CreateAsync( PlanInfo plan );

	void DeletePlan( string name );

	Task DeletePlanAsync( string name );

	void DeletePlanSteps( string name );

	Task DeletePlanStepsAsync( string name );

	StepScriptRepositoryKeys CreateStepScripts( string plan, string name, StepScripts? scripts );

	Task<StepScriptRepositoryKeys> CreateStepScriptsAsync( string plan, string name, StepScripts? scripts  );

	IObject<Stream>? GetStepDeployScript( string plan, string name );

	IObject<Stream>? GetStepVerifyScript( string plan, string name );

	IObject<Stream>? GetStepRollbackScript( string plan, string name );

	StepScriptRepositoryKeys SetStepScripts( string plan, string name, StepScripts scripts);

	Task<StepScriptRepositoryKeys> SetStepScriptsAsync( string plan, string name, StepScripts scripts );

	void DeleteStepScripts( string plan, string name );

	Task DeleteStepScriptsAsync( string plan, string name );
}
*/

public record StepScriptsTemplateConfiguration( string DeployTemplate, string VerifyTemplate, string RollbackTemplate );

public class Project : IProject
{
	private readonly IProjectInfoStore _infoStore;
	private readonly IScriptsStore _scriptsStore;
	private readonly IReleaseDependencyDeterminator _releaseDependencyDeterminator;

	private readonly SemaphoreSlim _plansSemaphore = new ( 1, 1 ),
									_releasesSemaphore = new ( 1, 1 ),
									_journalingSemaphore = new ( 1, 1 );

	// null for a project opened read-only, which has no user to record changes against
	private readonly UserInfo? _userInfo;

	internal Project( IProjectInfoStore infoStore, IScriptsStore scriptsStore, IReleaseDependencyDeterminator dependencyDeterminator, UserInfo? userInfo )
	{
		_infoStore = infoStore ?? throw new ArgumentNullException( nameof(infoStore) );
		_scriptsStore = scriptsStore ?? throw new ArgumentNullException( nameof(scriptsStore) );
		_releaseDependencyDeterminator = dependencyDeterminator ?? throw new ArgumentNullException( nameof(dependencyDeterminator) );
		_userInfo = userInfo;
	}

	private ActionContext GetActionContext()
	{
		// only writes record an action context, and a read-only project is handed out as
		// IReadOnlyProject, so reaching here without a user is a caller casting it back
		if( _userInfo is null )
			throw new InvalidOperationException( "The project was opened read-only and cannot record changes." );

		return new ActionContext(
				_userInfo,
				DateTime.Now
			);
	}

	// set once by initialization and never changed afterwards, so no semaphore or transaction
	public ProjectDefinition GetDefinition() => _infoStore.GetDefinition();

	private void EnsurePlanNotFinalized( ProjectPlan plan, SemaphoreSlim? semaphore )
	{
		if( string.IsNullOrEmpty( plan.Release ) )
			return;

		ProjectRelease? release = _infoStore.GetRelease( plan.Release );

		if( release is null || release.Finalized is null )
			return;

		semaphore?.Release();

		throw new PlanFinalizedException();
	}

	private void Register( PlanDefinition plan, string? releaseName )
	{
        ArgumentNullException.ThrowIfNull(plan, nameof(plan));

		ArgumentException.ThrowIfNullOrWhiteSpace( plan.Name, nameof(plan.Name));

        plan.Validate();

		if( _infoStore.PlanExists( plan.Name ) )
			throw new DuplicatePlanNameException();

		if( plan.Dependencies.Count != 0 )
		{
			IEnumerable<string> dependablePlans = _infoStore.GetPlans( releaseName ).Values.Select( x => x.Definition.Name);

			foreach( string dependency in plan.Dependencies )
			{
				if( !dependablePlans.Contains( dependency ) )
					throw new UnknownPlanException( dependency );
			}
		}

		_infoStore.AddPlan( plan, plan.Dependencies, GetActionContext() );

		if( releaseName is null )
			return;

		ProjectRelease? release = _infoStore.GetRelease( releaseName );

		if( release is null )
			throw new UnknownReleaseException(releaseName);

		IEnumerable<ProjectElement> existingPlans = _infoStore.GetReleasePlans( releaseName );

		EnsureNoMissingDependencies( existingPlans.Select( x => x.Definition.Name ).Append( plan.Name ), releaseName );
	}

	public void Add( PlanDefinition plan, string? release = null )
	{
		_plansSemaphore.Wait();

		using TransactionScope tx = new ();

		try
		{
			Register( plan, release );

			tx.Complete();
		}
		finally
		{
			_plansSemaphore.Release();
		}
	}

	public async Task AddAsync( PlanDefinition plan, string? release )
	{
		throw new NotImplementedException();
	}

	public IEnumerable<ProjectPlan> GetDependants( string plan )
	{
		return _infoStore.GetPlanDependants( plan ).Select( x => _infoStore.GetPlan( x ) );
	}

	public IEnumerable<ProjectPlan> GetDependencies( string plan )
	{
		return _infoStore.GetPlanDependencies( plan ).Select( x => _infoStore.GetPlan( x ) );
	}

	private bool Deregister( string name, SemaphoreSlim? semaphore )
	{
		if( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "Value cannot be null or whitespace.", nameof(name) );

		ProjectPlan? plan = _infoStore.GetPlan( name );

		if( plan is null  )
			return false;

		EnsurePlanNotFinalized( plan, semaphore );

		_infoStore.DeletePlan(name);

		return true;
	}

	public void DeletePlan( string name )
	{
		ArgumentNullException.ThrowIfNull( name, nameof(name) );

		_plansSemaphore.Wait();

		using TransactionScope tx = new ();

		try
		{
			// the finally block below releases the semaphore on every exit path,
			// so Deregister must not release it again before throwing
			Deregister( name, null );
			tx.Complete();
		}
		finally
		{
			_plansSemaphore.Release();
		}
	}

	public async Task DeletePlanAsync( string name )
	{
		ArgumentNullException.ThrowIfNull( name, nameof(name) );

		throw new NotImplementedException();
	}

	private StepScripts GetInitialStepScripts()
	{
		return new StepScripts( null, null, null );
	}

	public StepScriptKeys Add( StepDefinition step, string plan )
	{
        ArgumentNullException.ThrowIfNull(step, nameof(step));

		ArgumentNullException.ThrowIfNull( plan, nameof(plan) );

		step.Validate();

		_plansSemaphore.Wait();

		using TransactionScope tx = new ();

		ProjectPlan? planInfo = _infoStore.GetPlan( plan );

		if( planInfo is null )
		{
			_plansSemaphore.Release();
			throw new UnknownPlanException( plan );
		}

		EnsurePlanNotFinalized(planInfo, _plansSemaphore);

		IDictionary<string, ProjectElement> planSteps = _infoStore.GetSteps( plan );

		foreach( string dependency in step.Dependencies )
		{
			if( planSteps.ContainsKey( dependency ) ) continue;

			_plansSemaphore.Release();
			throw new MissingStepDependencyException( dependency );
		}

		StepScripts? scripts = null;
		StepScriptKeys scriptKeys;

		try
		{
			scripts = GetInitialStepScripts();
			scriptKeys = _scriptsStore.PutStepScripts( plan, step.Name, scripts );
			_infoStore.AddStep( plan, step, step.Dependencies, GetActionContext() );

			tx.Complete();
		}
		finally
		{
			_plansSemaphore.Release();
			scripts?.Dispose();
		}

		return scriptKeys;
	}

	public IEnumerable<ProjectElement> GetSteps( string plan )
	{
		ArgumentNullException.ThrowIfNull( plan, nameof(plan) );

		_plansSemaphore.Wait();

		using TransactionScope tx = new ();

		ProjectPlan? planInfo = _infoStore.GetPlan( plan );

		if( planInfo is null )
		{
			_plansSemaphore.Release();
			throw new UnknownPlanException( plan );
		}

		try
		{
			IDictionary<string, ProjectElement> planSteps = _infoStore.GetSteps( plan );

			tx.Complete();

			return DependencyOrdering.Sort(
				planSteps.Values,
				x => x.Definition.Name,
				x => ( x.Definition as StepDefinition )?.Dependencies ?? [],
				x => x.Registration.Timestamp );
		}
		finally
		{
			_plansSemaphore.Release();
		}

	}

	public StepScripts GetStepScripts( string plan, string step )
	{
		ArgumentNullException.ThrowIfNull( plan, nameof(plan) );

		ArgumentNullException.ThrowIfNull( step, nameof(step) );

		_plansSemaphore.Wait();

		using TransactionScope tx = new ();

		try
		{
			ProjectPlan? planInfo = _infoStore.GetPlan( plan );

			if( planInfo is null )
				throw new UnknownPlanException( plan );

			if( !_infoStore.GetSteps( plan ).ContainsKey( step ) )
				throw new UnknownStepException( plan, step );

			StepScripts? scripts = _scriptsStore.GetStepScripts( plan, step );

			List<string> missing = new ();

			if( scripts?.DeployScript is null )
				missing.Add( "deploy" );

			if( scripts?.VerifyScript is null )
				missing.Add( "verify" );

			if( scripts?.RollbackScript is null )
				missing.Add( "rollback" );

			if( missing.Count > 0 )
			{
				scripts?.Dispose();
				throw new MissingStepScriptsException( plan, step, missing );
			}

			tx.Complete();

			// the caller owns the returned streams
			return scripts!;
		}
		finally
		{
			_plansSemaphore.Release();
		}
	}

	public void VerifyStepScripts( string plan )
	{
		foreach( ProjectElement step in GetSteps( plan ) )
			using( GetStepScripts( plan, step.Definition.Name ) )
			{
			}
	}

	public void Delete( string plan, string step )
	{
		ArgumentNullException.ThrowIfNull( plan, nameof(plan) );

		ArgumentNullException.ThrowIfNull( step, nameof(step) );

		_plansSemaphore.Wait();

		ProjectPlan? projectPlan = _infoStore.GetPlan( plan );

		if( projectPlan is null )
		{
			_plansSemaphore.Release();
			throw new UnknownPlanException( plan );
		}

		EnsurePlanNotFinalized( projectPlan, _plansSemaphore );

		IEnumerable<string> dependants = _infoStore.GetStepDependants( plan, step );

		if( dependants.Any() )
		{
			_plansSemaphore.Release();
			throw new StepRequiredException( dependants );
		}

		using TransactionScope tx = new ();

		try
		{
			_infoStore.DeleteStep( plan, step );
			_scriptsStore.DeleteStepScripts(plan, step);

			tx.Complete();
		}
		finally
		{
			_plansSemaphore.Release();
		}
	}

	private ProjectPlan EnsurePlanExistsAsOrphan( string name )
	{
		ProjectPlan? plan = _infoStore.GetPlan( name );

		if( plan is null )
			throw new UnknownPlanException( name );

		if( !string.IsNullOrEmpty( plan.Release ) )
			throw new InvalidOperationException( $"Plan {name} is already part of another release" );

		return plan;
	}

	private IDictionary<string, (ProjectPlan, ICollection<string>)> DetermineMissingDependencies( IEnumerable<string> includedPlans, string release )
	{
		List<ProjectPlan> requiredPlans = [];
		Dictionary<string, (ProjectPlan, ICollection<string>)> missingDependencies = new ();

		foreach( string plan in includedPlans )
		{
			IEnumerable<string> planDependencies = _infoStore.GetPlanDependencies( plan );

			IEnumerable<ProjectPlan> dependencies =
				planDependencies
					.Where( x => !includedPlans.Contains( plan ) )
					.Select( x => _infoStore.GetPlan( x ) )
					.Where( x =>
								string.IsNullOrEmpty( x.Release ) ||
								_releaseDependencyDeterminator.DetermineDependencyRelationship( x.Release, release ) ==
								DependencyRelationship.Dependant
					);

			foreach( ProjectPlan dependency in dependencies )
			{
				ICollection<string>? dependants = null;

				if( missingDependencies.TryGetValue( dependency.Definition.Name, out (ProjectPlan, ICollection<string>) details ) )
				{
					details.Item2.Add(plan);
				}
				else
				{
					dependants = new List<string>() { plan };
					missingDependencies.Add(dependency.Definition.Name, (dependency, dependants));
				}
			}

			missingDependencies.Coalesce( DetermineMissingDependencies( planDependencies, release ), plan );

		}

		return missingDependencies;
	}

	private void EnsureNoMissingDependencies(IEnumerable<string> includedPlans, string release )
	{
		IDictionary<string, (ProjectPlan, ICollection<string>)> missingDependencies =
			DetermineMissingDependencies( includedPlans, release );

		if( missingDependencies.Any())
			throw new MissingPlanDependenciesException( missingDependencies );
	}

	private void EnumerateDependants( string plan, IEnumerable<ProjectElement> includedPlans, string release, Action<string, string> onDependantFound )
	{
		IEnumerable<string> dependants = _infoStore.GetPlanDependants( plan );

		HashSet<string> set = new (includedPlans.Select( x => x.Definition.Name ), null);

		foreach (string dependant in dependants )
		{
			// if dependant is in current release
			if (set.Remove(dependant))
			{
				onDependantFound( dependant, release );
			}
			else // add dependant to list if it is in a planned release
			{
				ProjectPlan dependantPlan = _infoStore.GetPlan( dependant )!;

				if( !string.IsNullOrEmpty( dependantPlan.Release ))
					onDependantFound( dependant, dependantPlan.Release );
			}
		}
	}

	private void EnsureNoAbandonedDependants( string plan, string release, IEnumerable<ProjectElement> includedPlans )
	{
		List<PlanDependant> dependantList = [];

		Action<string, string> onDependantFound = ( dependant, release ) =>
		{
			List<PlanDependant> planDependants;
			dependantList.Add( new PlanDependant( dependant, release ) );
		};

		EnumerateDependants( plan, includedPlans, release, onDependantFound );

		if( dependantList.Count == 0)
			return;

		throw new AbandonedPlanDependantsException( dependantList );
	}

	/// <summary>
	/// Every release except the project's very first must declare the release it depends
	/// on; the declared dependency must exist and must not already have a dependant, so
	/// releases form a single lineage.
	/// </summary>
	private void EnsureValidDependency( ReleaseDefinition definition )
	{
		if( string.IsNullOrWhiteSpace( definition.Dependency ) )
		{
			/*
			 New release must have dependency if at least one other release exists.
			 This also ensures dependency is not assigned later, which could cause circular dependencies.
			*/
			if( _infoStore.ReleaseExists() )
				throw new ReleaseDependencyRequiredException();

			return;
		}

		// Dependency must exist
		if( !_infoStore.ReleaseExists( definition.Dependency ) )
			throw new UnknownReleaseException( definition.Dependency );

		// Dependency must not already have a dependant, which would cause branching of releases
		ProjectRelease? dependant = _infoStore.GetReleaseDependant( definition.Dependency ).FirstOrDefault();

		if( dependant is not null )
			throw new ReleaseDependantExistsException( definition.Dependency, dependant.Definition.Name );
	}

	private void CreateRelease( ReleaseDefinition definition, IEnumerable<ProjectPlan> plans )
	{
		ActionContext actionContext = GetActionContext();

		using TransactionScope tx = new ();

		_infoStore.AddRelease( definition, actionContext );

		foreach( ProjectPlan plan in plans )
		{
			_infoStore.AddReleasePlan( definition.Name, plan.Definition.Name, actionContext );
		}

		tx.Complete();
	}

	public void CreateRelease( ReleaseDefinition definition, IEnumerable<string> plans )
	{
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(plans);

        definition.Validate();

		bool releasesLocked = false;

		_plansSemaphore.Wait();

		List<ProjectPlan> includedPlans = [];

		try
		{
			ProjectRelease? projectRelease = _infoStore.GetRelease( definition.Name );

			if( projectRelease is not null )
				throw new DuplicateReleaseNameException();

			includedPlans.AddRange( from planName in plans select EnsurePlanExistsAsOrphan( planName ) );

			EnsureNoMissingDependencies( plans, definition.Name );

			_releasesSemaphore.Wait();
			releasesLocked = true;

			EnsureValidDependency( definition );

			CreateRelease( definition, includedPlans );
		}
		finally
		{
			if( releasesLocked)
				_releasesSemaphore.Release();

			_plansSemaphore.Release();
		}
	}

	public void CreateRelease( ReleaseDefinition definition, bool includeUnreleasedPlans = false )
	{
        ArgumentNullException.ThrowIfNull(definition);

        definition.Validate();

		bool releasesLocked = false;

		_plansSemaphore.Wait();

		try
		{
			ProjectRelease? release = _infoStore.GetRelease(definition.Name);

			if( release is not null )
				throw new DuplicateReleaseNameException();

			IEnumerable<ProjectPlan> includedPlans =
				includeUnreleasedPlans ? _infoStore.GetPlans( string.Empty ).Values : Array.Empty<ProjectPlan>();

			_releasesSemaphore.Wait();
			releasesLocked = true;

			EnsureValidDependency( definition );

			CreateRelease( definition,  includedPlans);
		}
		finally
		{
			if( releasesLocked)
				_releasesSemaphore.Release();

			_plansSemaphore.Release();
		}
	}

	public IEnumerable<ProjectRelease> GetReleases( ReleaseSearchCriteria criteria )
	{
		ArgumentNullException.ThrowIfNull( criteria, nameof(criteria) );

		return DependencyOrdering.Sort(
			_infoStore.ListReleases( criteria ),
			x => x.Definition.Name,
			x => string.IsNullOrEmpty( x.Definition.Dependency ) ? [] : [x.Definition.Dependency],
			x => x.Registration.Timestamp );
	}

	public IEnumerable<ProjectRelease> GetReleaseDependants( string release )
	{
		return _infoStore.GetReleaseDependant( release );
	}

	public void AddPlanToRelease( string release, string plan, bool includeDependencies )
	{
		ValidateReleaseParameter( release );

		if( string.IsNullOrWhiteSpace( plan ) )
			throw new ArgumentException( "Value cannot be null or whitespace.", nameof(plan) );

		bool releasesLocked = false;
		ActionContext actionContext = GetActionContext();

		_plansSemaphore.Wait();

		using TransactionScope tx = new ();

		try
		{
			ProjectPlan? projectPlan = EnsurePlanExistsAsOrphan( plan );

			_releasesSemaphore.Wait();
			releasesLocked = true;

			ProjectRelease? projectRelease = _infoStore.GetRelease( release );

			if( projectRelease is null )
				throw new UnknownReleaseException(release);

			if( projectRelease.Finalized is not null )
				throw new ReleaseFinalizedException();

			IEnumerable<ProjectElement> existingPlans = _infoStore.GetReleasePlans( release );

			if( includeDependencies )
			{
				IDictionary<string, (ProjectPlan, ICollection<string>)> dependencies =
					DetermineMissingDependencies( existingPlans.Select( x => x.Definition.Name ), release );

				foreach( (ProjectPlan dependency, _) in dependencies.Values  )
				{
					if( string.IsNullOrEmpty( dependency.Release ) )
						_infoStore.AddReleasePlan( release, dependency.Definition.Name, actionContext );
					else if( _releaseDependencyDeterminator.DetermineDependencyRelationship( release, dependency.Release ) == DependencyRelationship.Dependency )
					{
						_infoStore.DeleteReleasePlan( dependency.Release,  dependency.Definition.Name, actionContext );
						_infoStore.AddReleasePlan( release, dependency.Definition.Name, actionContext );
					}

				}
			}
			else
				EnsureNoMissingDependencies( existingPlans.Select( x => x.Definition.Name ).Append( plan ), release );

			_infoStore.AddReleasePlan( release, plan, actionContext );

			tx.Complete();
		}
		finally
		{
			if(releasesLocked)
				_releasesSemaphore.Release();

			_plansSemaphore.Release();
		}
	}

	private static void ValidateReleaseParameter( string release )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( release, nameof(release) );
	}

	public IEnumerable<ProjectPlan> GetPlans( PlanSearchCriteria criteria )
	{
		ArgumentNullException.ThrowIfNull( criteria, nameof(criteria) );

		bool releasesLocked = false;

		_plansSemaphore.Wait();

		using TransactionScope tx = new ();

		try
		{
			_releasesSemaphore.Wait();

			releasesLocked = true;

			// the store returns released plans grouped by release in release-chronological
			// order; ordering the plans within each group is this layer's responsibility
			List<ProjectPlan> plans = [];

			foreach( List<ProjectPlan> releaseGroup in GroupByRelease( _infoStore.ListPlans( criteria ) ) )
				plans.AddRange(
					DependencyOrdering.Sort(
						releaseGroup,
						x => x.Definition.Name,
						x => ( x.Definition as PlanDefinition )?.Dependencies ?? [],
						x => x.Registration.Timestamp ) );

			tx.Complete();

			return plans;
		}
		finally
		{
			if(releasesLocked)
				_releasesSemaphore.Release();

			_plansSemaphore.Release();
		}
	}

	private static IEnumerable<List<ProjectPlan>> GroupByRelease( IEnumerable<ProjectPlan> plans )
	{
		List<ProjectPlan> group = [];

		foreach( ProjectPlan plan in plans )
		{
			if( group.Count > 0 && !string.Equals( group[^1].Release, plan.Release, StringComparison.OrdinalIgnoreCase ) )
			{
				yield return group;
				group = [];
			}

			group.Add( plan );
		}

		if( group.Count > 0 )
			yield return group;
	}

	public void RemovePlanFromRelease( string release, string plan, bool includeDependants = false )
	{
		ValidateReleaseParameter( release );

		if( string.IsNullOrWhiteSpace( plan ) )
			throw new ArgumentException( "Value cannot be null or whitespace.", nameof(plan) );

		bool releasesLocked = false;

		ActionContext actionContext = GetActionContext();

		_plansSemaphore.Wait();

		using TransactionScope tx = new ();

		try
		{
			_releasesSemaphore.Wait();

			releasesLocked = true;

			ProjectRelease? projectRelease = _infoStore.GetRelease( release );

			if( projectRelease is null )
				throw new UnknownReleaseException(release);

			if( projectRelease.Finalized is not null )
				throw new ReleaseFinalizedException();

			IEnumerable<ProjectElement> existingPlans = _infoStore.GetReleasePlans( release );

			if( !existingPlans.Any( x => x.Definition.Name.Equals( plan, StringComparison.InvariantCulture ) ) )
				return;

			if( includeDependants )
				EnumerateDependants(
					plan,
					existingPlans,
					release,
					( dependant, release ) => _infoStore.DeleteReleasePlan( release, dependant, actionContext )
				);
			else
				EnsureNoAbandonedDependants( plan, release, existingPlans );

			_infoStore.DeleteReleasePlan( release, plan, actionContext );

			tx.Complete();
		}
		finally
		{
			if(releasesLocked)
				_releasesSemaphore.Release();

			_plansSemaphore.Release();
		}
	}

	public void DeleteRelease( string release )
	{
		ValidateReleaseParameter( release );

		bool releasesLocked = false;
		ActionContext actionContext = GetActionContext();

		_plansSemaphore.Wait();

		using TransactionScope tx = new ();

		try
		{
			_releasesSemaphore.Wait();
			releasesLocked = true;

			ProjectRelease? projectRelease = _infoStore.GetRelease( release );

			if( projectRelease is null )
				throw new UnknownReleaseException(release);

			if( projectRelease.Finalized is not null )
				throw new ReleaseFinalizedException();

			IEnumerable<ProjectRelease> dependants = _infoStore.GetReleaseDependant( release );

			if( dependants.Any() )
				throw new ReleaseDependencyException( "Release with dependants cannot be deleted", dependants );

			IEnumerable<ProjectElement> existingPlans = _infoStore.GetReleasePlans( release );

			foreach( ProjectElement plan in existingPlans )
			{
				_infoStore.DeleteReleasePlan( release,  plan.Definition.Name, actionContext );
			}

			_infoStore.DeleteRelease( release );

			tx.Complete();
		}
		finally
		{
			if(releasesLocked)
				_releasesSemaphore.Release();

			_plansSemaphore.Release();
		}
	}

	private void EnsureNoOpenReleaseDependencies(string release)
	{
		IEnumerable<ProjectRelease> openReleases = _infoStore.ListReleases( new ReleaseSearchCriteria() );

		// finalizing requires every release this one (transitively) depends on to be
		// finalized already; open dependants are expected and do not block
		IEnumerable<ProjectRelease> releaseDependencies =
			from openRelease in openReleases
			where !openRelease.Definition.Name.Equals( release, StringComparison.InvariantCulture ) &&
				_releaseDependencyDeterminator.DetermineDependencyRelationship(
					release,
					openRelease.Definition.Name
				) == DependencyRelationship.Dependant
			select openRelease;

		if( releaseDependencies.Any() )
			throw new ReleaseDependencyException( "Unfinalized dependencies exist", releaseDependencies );
	}

	public void FinalizeRelease( string release )
	{
		ValidateReleaseParameter( release );

		bool releasesLocked = false;

		_plansSemaphore.Wait();

		using TransactionScope tx = new ();

		try
		{
			_releasesSemaphore.Wait();
			releasesLocked = true;

			ProjectRelease? projectRelease = _infoStore.GetRelease( release );

			if( projectRelease is null )
				throw new UnknownReleaseException(release);

			if( projectRelease.Finalized is not null )
				return;

			IDictionary<string, ProjectPlan> plans = _infoStore.GetPlans( release );

			if( !plans.Any() )
				throw new EmptyReleaseException();

			EnsureNoOpenReleaseDependencies( release );

			_infoStore.SetReleaseContext( release, GetActionContext() );

			tx.Complete();
		}

		finally
		{
			if(releasesLocked)
				_releasesSemaphore.Release();

			_plansSemaphore.Release();
		}

	}

	public string? GetJournalingStatement( JournalingSlot slot )
	{
		_journalingSemaphore.Wait();

		using TransactionScope tx = new ();

		try
		{
			string? statement = _infoStore.GetJournalingStatement( slot );

			tx.Complete();

			return statement;
		}
		finally
		{
			_journalingSemaphore.Release();
		}
	}

	public void SetJournalingStatement( JournalingSlot slot, string statement )
	{
		ArgumentNullException.ThrowIfNull( statement, nameof(statement) );

		// validate before it ever reaches the store, so a malformed statement cannot be set
		JournalingStatements.Parse( slot, statement );

		_journalingSemaphore.Wait();

		using TransactionScope tx = new ();

		try
		{
			_infoStore.SetJournalingStatement( slot, statement );

			tx.Complete();
		}
		finally
		{
			_journalingSemaphore.Release();
		}
	}

	public void VerifyJournalingStatements()
	{
		_journalingSemaphore.Wait();

		using TransactionScope tx = new ();

		try
		{
			Dictionary<JournalingSlot, string?> text = JournalingSlots.All.ToDictionary(
				slot => slot, slot => _infoStore.GetJournalingStatement( slot ) );

			IReadOnlyList<JournalingSlot> missing = JournalingStatements.Missing( text );

			if( missing.Count > 0 )
				throw new MissingJournalingStatementsException( missing );

			tx.Complete();
		}
		finally
		{
			_journalingSemaphore.Release();
		}
	}

	public void Dispose()
	{
		_infoStore.Dispose();
		_scriptsStore.Dispose();
		_plansSemaphore.Dispose();
		_journalingSemaphore.Dispose();

		GC.SuppressFinalize(this);
	}
}