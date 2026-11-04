using System.Transactions;

using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStore;

namespace Pug.Sqetch.Planning;

public record StepScriptsTemplateConfiguration( string DeployTemplate, string VerifyTemplate, string RollbackTemplate );

public class Project : IProject
{
	private readonly IProjectInfoStore _infoStore;
	private readonly IScriptsStore _scriptsStore;
	private readonly IReleaseDependencyDeterminator _releaseDependencyDeterminator;

	private readonly SemaphoreSlim _plansSemaphore = new( 1, 1 ),
									_releasesSemaphore = new( 1, 1 ),
									_journalingSemaphore = new( 1, 1 );

	// null for a project opened read-only, which has no user to record changes against
	private readonly UserInfo? _userInfo;

	internal Project(
		IProjectInfoStore infoStore,
		IScriptsStore scriptsStore,
		IReleaseDependencyDeterminator dependencyDeterminator,
		UserInfo? userInfo
	)
	{
		_infoStore = infoStore ?? throw new ArgumentNullException( nameof(infoStore) );
		_scriptsStore = scriptsStore ?? throw new ArgumentNullException( nameof(scriptsStore) );

		_releaseDependencyDeterminator =
			dependencyDeterminator ?? throw new ArgumentNullException( nameof(dependencyDeterminator) );

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
	public ProjectDefinition GetDefinition()
	{
		return _infoStore.GetDefinition();
	}

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
		ArgumentNullException.ThrowIfNull( plan );

		ArgumentException.ThrowIfNullOrWhiteSpace( plan.Name, nameof(ObjectDefinition.Name) );

		plan.Validate();

		if( _infoStore.PlanExists( plan.Name ) )
			throw new DuplicatePlanNameException();

		if( releaseName is not null && _infoStore.GetRelease( releaseName ) is null )
			throw new UnknownReleaseException( releaseName );

		// A dependency may be any existing plan; a plan meant for a release also needs each one in
		// that release or an earlier one. Both are checked before the plan is stored, since the
		// file store does not take part in the transaction and would keep a refused plan.
		Dictionary<string, (ProjectPlan, ICollection<string>)> missingDependencies =
			new( StringComparer.OrdinalIgnoreCase );

		foreach( string dependencyName in plan.Dependencies )
		{
			ProjectPlan dependency = _infoStore.GetPlan( dependencyName ) ??
									throw new UnknownPlanException( dependencyName );

			if( releaseName is not null && IsMissingFrom( releaseName, dependency ) )
				missingDependencies.TryAdd(
					dependency.Definition.Name,
					( dependency, new List<string> { plan.Name } )
				);
		}

		if( missingDependencies.Count != 0 )
			throw new MissingPlanDependenciesException( missingDependencies );

		_infoStore.AddPlan( plan, plan.Dependencies, GetActionContext() );
	}

	public void Add( PlanDefinition plan, string? release = null )
	{
		_plansSemaphore.Wait();

		using TransactionScope tx = new();

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

	public async Task AddAsync( PlanDefinition plan, string? release = null )
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

	private void Deregister( string name, SemaphoreSlim? semaphore )
	{
		if( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "Value cannot be null or whitespace.", nameof(name) );

		ProjectPlan? plan = _infoStore.GetPlan( name );

		EnsurePlanNotFinalized( plan, semaphore );

		_infoStore.DeletePlan( name );
	}

	public void DeletePlan( string name )
	{
		ArgumentNullException.ThrowIfNull( name );

		_plansSemaphore.Wait();

		using TransactionScope tx = new();

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
		ArgumentNullException.ThrowIfNull( name );

		throw new NotImplementedException();
	}

	private StepScripts GetInitialStepScripts()
	{
		return new StepScripts( null, null, null );
	}

	public StepScriptKeys Add( StepDefinition step, string plan )
	{
		ArgumentNullException.ThrowIfNull( step );

		ArgumentNullException.ThrowIfNull( plan );

		step.Validate();

		_plansSemaphore.Wait();

		using TransactionScope tx = new();

		ProjectPlan? planInfo = _infoStore.GetPlan( plan );

		if( planInfo is null )
		{
			_plansSemaphore.Release();

			throw new UnknownPlanException( plan );
		}

		EnsurePlanNotFinalized( planInfo, _plansSemaphore );

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
		ArgumentNullException.ThrowIfNull( plan );

		_plansSemaphore.Wait();

		using TransactionScope tx = new();

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
				x => ( x.Definition as StepDefinition )?.Dependencies ?? [ ],
				x => x.Registration.Timestamp
			);
		}
		finally
		{
			_plansSemaphore.Release();
		}

	}

	public StepScripts GetStepScripts( string plan, string step )
	{
		ArgumentNullException.ThrowIfNull( plan );

		ArgumentNullException.ThrowIfNull( step );

		_plansSemaphore.Wait();

		using TransactionScope tx = new();

		try
		{
			ProjectPlan? planInfo = _infoStore.GetPlan( plan );

			if( planInfo is null )
				throw new UnknownPlanException( plan );

			if( !_infoStore.GetSteps( plan ).ContainsKey( step ) )
				throw new UnknownStepException( plan, step );

			StepScripts? scripts = _scriptsStore.GetStepScripts( plan, step );

			List<string> missing = new();

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
		ArgumentNullException.ThrowIfNull( plan );

		ArgumentNullException.ThrowIfNull( step );

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

		using TransactionScope tx = new();

		try
		{
			_infoStore.DeleteStep( plan, step );
			_scriptsStore.DeleteStepScripts( plan, step );

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

	/// <summary>
	/// The dependencies <paramref name="includedPlans"/> would lack in <paramref name="release"/>:
	/// each one not among them that is either unreleased or in a release after it, keyed by name
	/// with the included plans needing it. Direct dependencies are enough: every included plan's
	/// own dependencies are checked, and a dependency outside the set sits in this release or an
	/// earlier one, where it was checked when it joined. A release being created ends the lineage,
	/// so for it only unreleased dependencies count.
	/// </summary>
	private IDictionary<string, (ProjectPlan, ICollection<string>)> DetermineMissingDependencies(
		IEnumerable<string> includedPlans,
		string release
	)
	{
		HashSet<string> included = new( includedPlans, StringComparer.OrdinalIgnoreCase );

		Dictionary<string, (ProjectPlan, ICollection<string>)> missingDependencies =
			new( StringComparer.OrdinalIgnoreCase );

		foreach( string plan in included )
		{
			foreach( string dependencyName in _infoStore.GetPlanDependencies( plan ) )
			{
				if( included.Contains( dependencyName ) )
					continue;

				ProjectPlan dependency = _infoStore.GetPlan( dependencyName ) ??
										throw new UnknownPlanException( dependencyName );

				if( !IsMissingFrom( release, dependency ) )
					continue;

				if( missingDependencies.TryGetValue(
						dependency.Definition.Name,
						out (ProjectPlan, ICollection<string>) details
					) )
					details.Item2.Add( plan );
				else
					missingDependencies.Add( dependency.Definition.Name, ( dependency, new List<string> { plan } ) );
			}
		}

		return missingDependencies;
	}

	/// <summary>
	/// The unreleased plans <paramref name="plan"/> depends on, directly or through other unreleased
	/// plans, dependencies before their dependants. Released plans end the walk: their own
	/// dependencies were settled when they joined their release.
	/// </summary>
	private List<string> CollectUnreleasedDependencies( ProjectPlan plan )
	{
		List<string> collected = [ ];
		HashSet<string> visited = new( StringComparer.OrdinalIgnoreCase ) { plan.Definition.Name };

		void Visit( string name )
		{
			foreach( string dependencyName in _infoStore.GetPlanDependencies( name ) )
			{
				if( !visited.Add( dependencyName ) )
					continue;

				ProjectPlan dependency = _infoStore.GetPlan( dependencyName ) ??
										throw new UnknownPlanException( dependencyName );

				if( !string.IsNullOrEmpty( dependency.Release ) )
					continue;

				Visit( dependency.Definition.Name );
				collected.Add( dependency.Definition.Name );
			}
		}

		Visit( plan.Definition.Name );

		return collected;
	}

	/// <summary>
	/// Whether a plan outside <paramref name="release"/> is unavailable to it: unreleased, or in a
	/// release that comes after it.
	/// </summary>
	private bool IsMissingFrom( string release, ProjectPlan dependency )
	{
		return string.IsNullOrEmpty( dependency.Release ) ||
				( _releaseDependencyDeterminator.DetermineDependencyRelationship( dependency.Release, release ) ==
				DependencyRelationship.Dependant );
	}

	private void EnsureNoMissingDependencies( IEnumerable<string> includedPlans, string release )
	{
		IDictionary<string, (ProjectPlan, ICollection<string>)> missingDependencies =
			DetermineMissingDependencies( includedPlans, release );

		if( missingDependencies.Any() )
			throw new MissingPlanDependenciesException( missingDependencies );
	}

	private void EnumerateDependants(
		string plan,
		IEnumerable<ProjectElement> includedPlans,
		string release,
		Action<string, string> onDependantFound
	)
	{
		IEnumerable<string> dependants = _infoStore.GetPlanDependants( plan );

		HashSet<string> set = new( includedPlans.Select( x => x.Definition.Name ), null );

		foreach( string dependant in dependants )

			// if dependant is in current release
			if( set.Remove( dependant ) )
			{
				onDependantFound( dependant, release );
			}
			else // add dependant to list if it is in a planned release
			{
				ProjectPlan dependantPlan = _infoStore.GetPlan( dependant )!;

				if( !string.IsNullOrEmpty( dependantPlan.Release ) )
					onDependantFound( dependant, dependantPlan.Release );
			}
	}

	private void EnsureNoAbandonedDependants( string plan, string release, IEnumerable<ProjectElement> includedPlans )
	{
		List<PlanDependant> dependantList = [ ];

		Action<string, string> onDependantFound = ( dependant, release ) => {
			dependantList.Add( new PlanDependant( dependant, release ) );
		};

		EnumerateDependants( plan, includedPlans, release, onDependantFound );

		if( dependantList.Count == 0 )
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

		using TransactionScope tx = new();

		_infoStore.AddRelease( definition, actionContext );

		foreach( ProjectPlan plan in plans )
			_infoStore.AddReleasePlan( definition.Name, plan.Definition.Name, actionContext );

		tx.Complete();
	}

	public void CreateRelease( ReleaseDefinition definition, IEnumerable<string> plans )
	{
		ArgumentNullException.ThrowIfNull( definition );
		ArgumentNullException.ThrowIfNull( plans );

		definition.Validate();

		bool releasesLocked = false;

		_plansSemaphore.Wait();

		List<ProjectPlan> includedPlans = [ ];

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
			if( releasesLocked )
				_releasesSemaphore.Release();

			_plansSemaphore.Release();
		}
	}

	public void CreateRelease( ReleaseDefinition definition, bool includeUnreleasedPlans = false )
	{
		ArgumentNullException.ThrowIfNull( definition );

		definition.Validate();

		bool releasesLocked = false;

		_plansSemaphore.Wait();

		try
		{
			ProjectRelease? release = _infoStore.GetRelease( definition.Name );

			if( release is not null )
				throw new DuplicateReleaseNameException();

			IEnumerable<ProjectPlan> includedPlans =
				includeUnreleasedPlans ? _infoStore.GetPlans( string.Empty ).Values : Array.Empty<ProjectPlan>();

			_releasesSemaphore.Wait();
			releasesLocked = true;

			EnsureValidDependency( definition );

			CreateRelease( definition, includedPlans );
		}
		finally
		{
			if( releasesLocked )
				_releasesSemaphore.Release();

			_plansSemaphore.Release();
		}
	}

	public IEnumerable<ProjectRelease> GetReleases( ReleaseSearchCriteria criteria )
	{
		ArgumentNullException.ThrowIfNull( criteria );

		return DependencyOrdering.Sort(
			_infoStore.ListReleases( criteria ),
			x => x.Definition.Name,
			x => string.IsNullOrEmpty( x.Definition.Dependency ) ? [ ] : [ x.Definition.Dependency ],
			x => x.Registration.Timestamp
		);
	}

	public IEnumerable<ProjectRelease> GetReleaseDependants( string release )
	{
		return _infoStore.GetReleaseDependant( release );
	}

	public void AddPlanToRelease( string release, string plan, bool includeDependencies )
	{
		Project.ValidateReleaseParameter( release );

		if( string.IsNullOrWhiteSpace( plan ) )
			throw new ArgumentException( "Value cannot be null or whitespace.", nameof(plan) );

		bool releasesLocked = false;
		ActionContext actionContext = GetActionContext();

		_plansSemaphore.Wait();

		using TransactionScope tx = new();

		try
		{
			ProjectPlan projectPlan = EnsurePlanExistsAsOrphan( plan );

			_releasesSemaphore.Wait();
			releasesLocked = true;

			ProjectRelease? projectRelease = _infoStore.GetRelease( release );

			if( projectRelease is null )
				throw new UnknownReleaseException( release );

			if( projectRelease.Finalized is not null )
				throw new ReleaseFinalizedException();

			IEnumerable<ProjectElement> existingPlans = _infoStore.GetReleasePlans( release );

			// with dependencies: every unreleased plan this one needs, directly or through another
			// unreleased plan, joins it; one in a later release is refused below rather than moved
			List<string> unreleasedDependencies =
				includeDependencies ? CollectUnreleasedDependencies( projectPlan ) : [ ];

			EnsureNoMissingDependencies(
				existingPlans.Select( x => x.Definition.Name ).Concat( unreleasedDependencies ).Append( plan ),
				release
			);

			foreach( string dependency in unreleasedDependencies )
				_infoStore.AddReleasePlan( release, dependency, actionContext );

			_infoStore.AddReleasePlan( release, plan, actionContext );

			tx.Complete();
		}
		finally
		{
			if( releasesLocked )
				_releasesSemaphore.Release();

			_plansSemaphore.Release();
		}
	}

	public void MovePlanToRelease( string plan, string release, bool includeDependencies )
	{
		Project.ValidateReleaseParameter( release );

		if( string.IsNullOrWhiteSpace( plan ) )
			throw new ArgumentException( "Value cannot be null or whitespace.", nameof(plan) );

		bool releasesLocked = false;
		ActionContext actionContext = GetActionContext();

		_plansSemaphore.Wait();

		using TransactionScope tx = new();

		try
		{
			ProjectPlan projectPlan = _infoStore.GetPlan( plan ) ?? throw new UnknownPlanException( plan );

			_releasesSemaphore.Wait();
			releasesLocked = true;

			ProjectRelease target = _infoStore.GetRelease( release ) ?? throw new UnknownReleaseException( release );

			if( target.Finalized is not null )
				throw new ReleaseFinalizedException();

			string source = projectPlan.Release;

			if( string.Equals( source, release, StringComparison.OrdinalIgnoreCase ) )
				return;

			if( source.Length > 0 && _infoStore.GetRelease( source )?.Finalized is not null )
				throw new ReleaseFinalizedException();

			// the file store takes no part in the transaction, so everything is checked before the
			// first write: a refused move must leave the plan in its release, not unreleased
			List<string> unreleasedDependencies =
				includeDependencies ? CollectUnreleasedDependencies( projectPlan ) : [ ];

			EnsureNoMissingDependencies(
				_infoStore.GetReleasePlans( release )
					.Select( x => x.Definition.Name )
					.Concat( unreleasedDependencies )
					.Append( plan ),
				release
			);

			EnsureNoStrandedDependants( projectPlan.Definition.Name, release );

			foreach( string dependency in unreleasedDependencies )
				_infoStore.AddReleasePlan( release, dependency, actionContext );

			_infoStore.SetPlanRelease( projectPlan.Definition.Name, release, actionContext );

			tx.Complete();
		}
		finally
		{
			if( releasesLocked )
				_releasesSemaphore.Release();

			_plansSemaphore.Release();
		}
	}

	/// <summary>
	/// Refuses moving <paramref name="plan"/> to <paramref name="release"/> when a plan depending on
	/// it sits in an earlier release, where it would deploy before the plan it needs. Unreleased
	/// dependants and those in the release or a later one are unaffected.
	/// </summary>
	private void EnsureNoStrandedDependants( string plan, string release )
	{
		List<PlanDependant> stranded = [ ];

		foreach( string dependantName in _infoStore.GetPlanDependants( plan ) )
		{
			ProjectPlan dependant = _infoStore.GetPlan( dependantName ) ?? throw new UnknownPlanException( dependantName );

			if( string.IsNullOrEmpty( dependant.Release ) )
				continue;

			if( _releaseDependencyDeterminator.DetermineDependencyRelationship( release, dependant.Release ) ==
				DependencyRelationship.Dependant )
				stranded.Add( new PlanDependant( dependant.Definition.Name, dependant.Release ) );
		}

		if( stranded.Count > 0 )
			throw new StrandedPlanDependantsException( plan, release, stranded );
	}

	private static void ValidateReleaseParameter( string release )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( release );
	}

	public IEnumerable<ProjectPlan> GetPlans( PlanSearchCriteria criteria )
	{
		ArgumentNullException.ThrowIfNull( criteria );

		bool releasesLocked = false;

		_plansSemaphore.Wait();

		using TransactionScope tx = new();

		try
		{
			_releasesSemaphore.Wait();

			releasesLocked = true;

			// the store returns released plans grouped by release in release-chronological
			// order; ordering the plans within each group is this layer's responsibility
			List<ProjectPlan> plans = [ ];

			foreach( List<ProjectPlan> releaseGroup in Project.GroupByRelease( _infoStore.ListPlans( criteria ) ) )
				plans.AddRange(
					DependencyOrdering.Sort(
						releaseGroup,
						x => x.Definition.Name,
						x => ( x.Definition as PlanDefinition )?.Dependencies ?? [ ],
						x => x.Registration.Timestamp
					)
				);

			tx.Complete();

			return plans;
		}
		finally
		{
			if( releasesLocked )
				_releasesSemaphore.Release();

			_plansSemaphore.Release();
		}
	}

	private static IEnumerable<List<ProjectPlan>> GroupByRelease( IEnumerable<ProjectPlan> plans )
	{
		List<ProjectPlan> group = [ ];

		foreach( ProjectPlan plan in plans )
		{
			if( ( group.Count > 0 ) &&
				!string.Equals( group[^1].Release, plan.Release, StringComparison.OrdinalIgnoreCase ) )
			{
				yield return group;

				group = [ ];
			}

			group.Add( plan );
		}

		if( group.Count > 0 )
			yield return group;
	}

	public void RemovePlanFromRelease( string release, string plan, bool includeDependants = false )
	{
		Project.ValidateReleaseParameter( release );

		if( string.IsNullOrWhiteSpace( plan ) )
			throw new ArgumentException( "Value cannot be null or whitespace.", nameof(plan) );

		bool releasesLocked = false;

		ActionContext actionContext = GetActionContext();

		_plansSemaphore.Wait();

		using TransactionScope tx = new();

		try
		{
			_releasesSemaphore.Wait();

			releasesLocked = true;

			ProjectRelease? projectRelease = _infoStore.GetRelease( release );

			if( projectRelease is null )
				throw new UnknownReleaseException( release );

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
			if( releasesLocked )
				_releasesSemaphore.Release();

			_plansSemaphore.Release();
		}
	}

	public void DeleteRelease( string release )
	{
		Project.ValidateReleaseParameter( release );

		bool releasesLocked = false;
		ActionContext actionContext = GetActionContext();

		_plansSemaphore.Wait();

		using TransactionScope tx = new();

		try
		{
			_releasesSemaphore.Wait();
			releasesLocked = true;

			ProjectRelease? projectRelease = _infoStore.GetRelease( release );

			if( projectRelease is null )
				throw new UnknownReleaseException( release );

			if( projectRelease.Finalized is not null )
				throw new ReleaseFinalizedException();

			IEnumerable<ProjectRelease> dependants = _infoStore.GetReleaseDependant( release );

			if( dependants.Any() )
				throw new ReleaseDependencyException( "Release with dependants cannot be deleted", dependants );

			IEnumerable<ProjectElement> existingPlans = _infoStore.GetReleasePlans( release );

			foreach( ProjectElement plan in existingPlans )
				_infoStore.DeleteReleasePlan( release, plan.Definition.Name, actionContext );

			_infoStore.DeleteRelease( release );

			tx.Complete();
		}
		finally
		{
			if( releasesLocked )
				_releasesSemaphore.Release();

			_plansSemaphore.Release();
		}
	}

	private void EnsureNoOpenReleaseDependencies( string release )
	{
		IEnumerable<ProjectRelease> openReleases = _infoStore.ListReleases( new ReleaseSearchCriteria() );

		// finalizing requires every release this one (transitively) depends on to be
		// finalized already; open dependants are expected and do not block
		IEnumerable<ProjectRelease> releaseDependencies =
			from openRelease in openReleases
			where !openRelease.Definition.Name.Equals( release, StringComparison.InvariantCulture ) &&
				( _releaseDependencyDeterminator.DetermineDependencyRelationship(
					release,
					openRelease.Definition.Name
				) ==
				DependencyRelationship.Dependant )
			select openRelease;

		if( releaseDependencies.Any() )
			throw new ReleaseDependencyException( "Unfinalized dependencies exist", releaseDependencies );
	}

	public void FinalizeRelease( string release )
	{
		Project.ValidateReleaseParameter( release );

		bool releasesLocked = false;

		_plansSemaphore.Wait();

		using TransactionScope tx = new();

		try
		{
			_releasesSemaphore.Wait();
			releasesLocked = true;

			ProjectRelease? projectRelease = _infoStore.GetRelease( release );

			if( projectRelease is null )
				throw new UnknownReleaseException( release );

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
			if( releasesLocked )
				_releasesSemaphore.Release();

			_plansSemaphore.Release();
		}

	}

	public string? GetJournalingStatement( JournalingSlot slot )
	{
		_journalingSemaphore.Wait();

		using TransactionScope tx = new();

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
		ArgumentNullException.ThrowIfNull( statement );

		// validate before it ever reaches the store, so a malformed statement cannot be set
		JournalingStatements.Parse( slot, statement );

		_journalingSemaphore.Wait();

		using TransactionScope tx = new();

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

		using TransactionScope tx = new();

		try
		{
			Dictionary<JournalingSlot, string?> text = JournalingSlots.All.ToDictionary(
				slot => slot,
				slot => _infoStore.GetJournalingStatement( slot )
			);

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

		GC.SuppressFinalize( this );
	}
}