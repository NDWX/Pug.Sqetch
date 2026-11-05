using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStore;

namespace Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

/// <summary>
/// Git-friendly file-system implementation of <see cref="IProjectInfoStore"/>. Unreleased
/// plans live under 'plans/'; adding a plan to a release physically moves its folder under
/// the release (so git shows the assignment as a rename and the root 'plans/' folder only
/// ever holds in-flight work); finalizing a release moves the release folder from the top
/// of 'releases/' into its shard, and the committed 'plan-index' file locates any plan
/// ever created without scanning release folders.
/// </summary>
public sealed class FileSystemProjectInfoStore : IProjectInfoStore
{
	private readonly ProjectStoreSession _session;

	internal FileSystemProjectInfoStore( ProjectStoreSession session )
	{
		_session = session;
	}

	public ProjectDefinition GetDefinition()
		=> new( _session.Project.Name, _session.Project.Description, _session.Project.Engine );

	public void AddPlan( ObjectDefinition definition, IEnumerable<string> dependencies, ActionContext context )
	{
		NameRules.Ensure( definition.Name, "Plan" );

		if( _session.PlanIndex.Contains( definition.Name ) )
			throw new DuplicatePlanNameException();

		string[] dependencyNames = dependencies.ToArray();

		JsonFiles.Write(
			_session.Paths.PlanFile( string.Empty, definition.Name ),
			new PlanDocument(
				definition.Name,
				definition.Description,
				dependencyNames,
				string.Empty,
				ActionDocument.From( context ),
				null
			)
		);

		_session.PlanIndex.Set( new PlanIndexEntry( definition.Name, string.Empty, dependencyNames ) );
	}

	public void UpdatePlan( PlanDefinition definition )
	{
		( PlanIndexEntry entry, string planFile, PlanDocument document ) = RequirePlanDocument( definition.Name );

		_session.EnsureReleaseNotFinalized( entry.Release );

		string[] dependencyNames = definition.Dependencies.ToArray();

		// plan.json is the truth and is written whole, in one step; the index follows and is
		// rebuilt from plan.json by a reindex if a crash comes between the two
		JsonFiles.Write(
			planFile,
			document with { Description = definition.Description, Dependencies = dependencyNames }
		);

		_session.PlanIndex.Set( entry with { Dependencies = dependencyNames } );
	}

	public bool PlanExists( string name ) => _session.PlanIndex.Contains( name );

	public IDictionary<string, ProjectPlan> GetPlans( string? release = null )
	{
		IEnumerable<PlanIndexEntry> entries = release is null ?
			_session.PlanIndex.Entries :
			_session.PlanIndex.Entries.Where( x => string.Equals(
					x.Release,
					release,
					StringComparison.OrdinalIgnoreCase
				)
			);

		Dictionary<string, ProjectPlan> plans = new( StringComparer.OrdinalIgnoreCase );

		foreach( PlanIndexEntry entry in entries )
			plans[entry.Name] = ReadPlanDocument( entry ).ToModel();

		return plans;
	}

	public ProjectPlan? GetPlan( string name )
	{
		PlanIndexEntry? entry = _session.PlanIndex.Get( name );

		return entry is null ? null : ReadPlanDocument( entry ).ToModel();
	}

	public IEnumerable<string> GetPlanDependencies( string name )
		=> _session.RequirePlan( name ).Dependencies;

	public IEnumerable<string> GetPlanDependants( string name )
		=> _session.PlanIndex.Entries
			.Where( x => x.Dependencies.Contains( name, StringComparer.OrdinalIgnoreCase ) )
			.Select( x => x.Name )
			.ToArray();

	public void AddStep(
		string plan,
		ObjectDefinition definition,
		IEnumerable<string> dependencies,
		ActionContext context
	)
	{
		NameRules.Ensure( definition.Name, "Step" );

		PlanIndexEntry entry = _session.RequirePlan( plan );

		_session.EnsureReleaseNotFinalized( entry.Release );

		if( FindStepDirectory( entry, definition.Name ) is not null )
			throw new DuplicateStepNameException();

		JsonFiles.Write(
			_session.Paths.StepFile( entry.Release, entry.Name, definition.Name ),
			new StepDocument(
				definition.Name,
				definition.Description,
				dependencies.ToArray(),
				ActionDocument.From( context )
			)
		);
	}

	public void UpdateStep( string plan, ObjectDefinition definition )
	{
		( PlanIndexEntry entry, string stepFile, StepDocument document ) = RequireStepDocument( plan, definition.Name );

		_session.EnsureReleaseNotFinalized( entry.Release );

		JsonFiles.Write( stepFile, document with { Description = definition.Description } );
	}

	public void RenameStep( string plan, string name, string newName )
	{
		NameRules.Ensure( newName, "Step" );

		PlanIndexEntry entry = _session.RequirePlan( plan );

		_session.EnsureReleaseNotFinalized( entry.Release );

		string stepDirectory = FindStepDirectory( entry, name ) ?? throw new UnknownStepException( entry.Name, name );
		string oldName = Path.GetFileName( stepDirectory );

		if( FindStepDirectory( entry, newName ) is not null )
			throw new DuplicateStepNameException();

		string target = _session.Paths.StepDirectory( entry.Release, entry.Name, newName );

		if( Directory.Exists( target ) )
			throw new ProjectStoreException(
				$"'{_session.Paths.RelativeToRoot( target )}' already exists but is not a step; remove it first."
			);

		List<(string StepFile, StepDocument Document)> dependants = EnumerateSteps( entry )
			.Where( x => x.Document.Dependencies.Contains( oldName, StringComparer.OrdinalIgnoreCase ) )
			.Select( x => ( Path.Combine( x.Directory, FileNames.StepFile ), x.Document ) )
			.ToList();

		// 1. dependants name the new step beside the old one: a dependency naming no step is ignored
		//    by the ordering, so until the folder moves nothing changes
		foreach( (string stepFile, StepDocument document) in dependants )
			JsonFiles.Write(
				stepFile,
				document with { Dependencies = WithBeside( document.Dependencies, oldName, newName ) }
			);

		// 2. the commit point: one rename, scripts included, so git sees a rename
		Directory.Move( stepDirectory, target );

		// 3. and 4. tidy up; a reindex does the same if these are cut short
		string movedFile = Path.Combine( target, FileNames.StepFile );

		JsonFiles.Write( movedFile, JsonFiles.Read<StepDocument>( movedFile ) with { Name = newName } );

		foreach( (string stepFile, StepDocument document) in dependants )
			JsonFiles.Write(
				stepFile,
				document with
				{
					Dependencies = WithBeside( document.Dependencies, oldName, newName )
						.Where( x => !string.Equals( x, oldName, StringComparison.OrdinalIgnoreCase ) )
						.ToArray()
				}
			);
	}

	// newName right after oldName, keeping the list's order
	private static string[] WithBeside( string[] dependencies, string oldName, string newName )
		=> dependencies.Contains( newName, StringComparer.Ordinal ) ?
			dependencies :
			dependencies.SelectMany( x => string.Equals( x, oldName, StringComparison.OrdinalIgnoreCase ) ?
					new[] { x, newName } :
					[ x ]
				)
				.ToArray();

	public bool StepExists( string plan, string name )
	{
		PlanIndexEntry? entry = _session.PlanIndex.Get( plan );

		return entry is not null && FindStepDirectory( entry, name ) is not null;
	}

	public IDictionary<string, ProjectElement> GetSteps( string plan )
	{
		PlanIndexEntry entry = _session.RequirePlan( plan );

		Dictionary<string, ProjectElement> steps = new( StringComparer.OrdinalIgnoreCase );

		foreach( StepDocument document in EnumerateStepDocuments( entry ) )
			steps[document.Name] = document.ToModel( entry.Name );

		return steps;
	}

	public ProjectElement? GetStep( string plan, string name )
	{
		PlanIndexEntry? entry = _session.PlanIndex.Get( plan );

		if( entry is null )
			return null;

		string? stepDirectory = FindStepDirectory( entry, name );

		return stepDirectory is null ? null : ReadStepDocument( stepDirectory ).ToModel( entry.Name );
	}

	public IEnumerable<string> GetStepDependencies( string plan, string name )
	{
		( _, _, StepDocument document ) = RequireStepDocument( plan, name );

		return document.Dependencies;
	}

	public void SetStepDependencies( string plan, string name, IEnumerable<string> dependencies )
	{
		( PlanIndexEntry entry, string stepFile, StepDocument document ) = RequireStepDocument( plan, name );

		_session.EnsureReleaseNotFinalized( entry.Release );

		JsonFiles.Write( stepFile, document with { Dependencies = dependencies.ToArray() } );
	}

	public IEnumerable<string> GetStepDependants( string plan, string name )
	{
		PlanIndexEntry entry = _session.RequirePlan( plan );

		return EnumerateStepDocuments( entry )
			.Where( x => x.Dependencies.Contains( name, StringComparer.OrdinalIgnoreCase ) )
			.Select( x => x.Name )
			.ToArray();
	}

	public void DeletePlan( string name )
	{
		PlanIndexEntry? entry = _session.PlanIndex.Get( name );

		if( entry is null )
			return;

		_session.EnsureReleaseNotFinalized( entry.Release );

		string planDirectory = _session.Paths.PlanDirectory( entry.Release, entry.Name );

		if( Directory.Exists( planDirectory ) )
			Directory.Delete( planDirectory, recursive: true );

		_session.PlanIndex.Remove( entry.Name );
	}

	public void DeleteStep( string plan, string name )
	{
		PlanIndexEntry entry = _session.RequirePlan( plan );

		_session.EnsureReleaseNotFinalized( entry.Release );

		string? stepDirectory = FindStepDirectory( entry, name );

		if( stepDirectory is not null )
			Directory.Delete( stepDirectory, recursive: true );
	}

	public bool ReleaseExists( string? name = null )
		=> string.IsNullOrWhiteSpace( name ) ?
			_session.Paths.EnumerateReleaseDirectories( string.Empty ).Any() :
			_session.ReadRelease( name ) is not null;

	public IEnumerable<ProjectPlan> ListPlans( PlanSearchCriteria criteria )
	{
		if( criteria.Release is not null )
			return ListReleasePlans( criteria.Release, criteria );

		if( criteria.Released || criteria.ReleaseFinalizeTimestamp is not null )
			return ListReleasedPlans( criteria );

		return ListUnreleasedPlans( criteria );
	}

	public IEnumerable<ProjectRelease> ListReleases( ReleaseSearchCriteria criteria )
	{
		List<ProjectRelease> releases = [ ];

		foreach( ReleaseDocument document in MatchReleaseDocuments( criteria ) )
			releases.Add( document.ToModel() );

		return releases;
	}

	public ProjectRelease? GetRelease( string name ) => _session.ReadRelease( name )?.ToModel();

	public void AddRelease( ObjectDefinition definition, ActionContext context )
	{
		NameRules.Ensure( definition.Name, "Release" );

		if( _session.ReadRelease( definition.Name ) is not null )
			throw new DuplicateReleaseNameException();

		string dependency = ( definition as ReleaseDefinition )?.Dependency ?? string.Empty;

		JsonFiles.Write(
			_session.Paths.ReleaseFile( definition.Name ),
			new ReleaseDocument(
				definition.Name,
				definition.Description,
				dependency,
				ActionDocument.From( context ),
				null
			)
		);
	}

	public void UpdateRelease( ObjectDefinition definition )
	{
		ReleaseDocument document = _session.RequireRelease( definition.Name );

		if( document.Finalized is not null )
			throw new ReleaseFinalizedException();

		JsonFiles.Write( _session.Paths.ReleaseFile( document.Name ), document with { Description = definition.Description } );
	}

	public void DeleteRelease( string name )
	{
		ReleaseDocument document = _session.RequireRelease( name );

		if( document.Finalized is not null )
			throw new ReleaseFinalizedException();

		string plansDirectory = _session.Paths.ReleasePlansDirectory( document.Name );

		if( Directory.Exists( plansDirectory ) &&
			Directory.EnumerateDirectories( plansDirectory )
				.Any( x => File.Exists( Path.Combine( x, FileNames.PlanFile ) ) ) )
			throw new ProjectStoreException( $"Release '{document.Name}' still contains plans." );

		Directory.Delete( _session.Paths.ReleaseDirectory( document.Name ), recursive: true );
	}

	public void SetReleaseContext( string release, ActionContext releaseContext )
	{
		ReleaseDocument document = _session.RequireRelease( release );

		if( document.Finalized is not null )
			throw new ReleaseFinalizedException();

		string source = _session.Paths.ReleaseDirectory( document.Name );

		JsonFiles.Write(
			Path.Combine( source, FileNames.ReleaseFile ),
			document with { Finalized = ActionDocument.From( releaseContext ) }
		);

		MoveToFinalizedLocation( document.Name, source );
	}

	// finalizing a release moves its folder into the shard; unfinalized releases live
	// unsharded because there are only ever a few of them at a time
	private void MoveToFinalizedLocation( string release, string source )
	{
		string target = _session.Paths.FinalizedReleaseDirectory( release );

		if( string.Equals( target, source, StringComparison.Ordinal ) )
			return;

		// a release named like its own shard finalizes into a child of its current folder,
		// which Directory.Move cannot do directly; hop through a temporary sibling
		if( ( target + Path.DirectorySeparatorChar ).StartsWith(
				source + Path.DirectorySeparatorChar,
				StringComparison.Ordinal
			) )
		{
			string temporary = source + ".finalizing";

			Directory.Move( source, temporary );
			Directory.CreateDirectory( Path.GetDirectoryName( target )! );
			Directory.Move( temporary, target );

			return;
		}

		Directory.CreateDirectory( Path.GetDirectoryName( target )! );
		Directory.Move( source, target );
	}

	public IEnumerable<ProjectRelease> GetReleaseDependant( string release )
		=> EnumerateReleaseDocuments( string.Empty )
			.Where( x => string.Equals( x.Dependency, release, StringComparison.OrdinalIgnoreCase ) )
			.Select( x => x.ToModel() )
			.ToArray();

	public IEnumerable<ProjectElement> GetReleasePlans( string release )
	{
		ReleaseDocument document = _session.RequireRelease( release );

		string plansDirectory = _session.Paths.ReleasePlansDirectory( document.Name );

		if( !Directory.Exists( plansDirectory ) )
			return [ ];

		List<ProjectElement> plans = [ ];

		foreach( string planDirectory in Directory.EnumerateDirectories( plansDirectory ) )
		{
			PlanDocument? plan = JsonFiles.TryRead<PlanDocument>( Path.Combine( planDirectory, FileNames.PlanFile ) );

			if( plan is not null )
				plans.Add( plan.ToModel() );
		}

		return plans;
	}

	public void AddReleasePlan( string release, string name, ActionContext context )
	{
		PlanIndexEntry entry = _session.RequirePlan( name );

		if( !string.IsNullOrEmpty( entry.Release ) )
			throw new InvalidOperationException( $"Plan '{name}' is already part of release '{entry.Release}'." );

		ReleaseDocument releaseDocument = _session.RequireRelease( release );

		if( releaseDocument.Finalized is not null )
			throw new ReleaseFinalizedException();

		MovePlan( entry, entry.Release, releaseDocument.Name, ActionDocument.From( context ) );
	}

	public void DeleteReleasePlan( string release, string name, ActionContext context )
	{
		PlanIndexEntry entry = _session.RequirePlan( name );

		if( !string.Equals( entry.Release, release, StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidOperationException( $"Plan '{name}' is not part of release '{release}'." );

		ReleaseDocument releaseDocument = _session.RequireRelease( release );

		if( releaseDocument.Finalized is not null )
			throw new ReleaseFinalizedException();

		MovePlan( entry, entry.Release, string.Empty, null );
	}

	public void SetPlanRelease( string name, string release, ActionContext context )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( release );

		PlanIndexEntry entry = _session.RequirePlan( name );

		ReleaseDocument releaseDocument = _session.RequireRelease( release );

		if( releaseDocument.Finalized is not null )
			throw new ReleaseFinalizedException();

		if( string.Equals( entry.Release, releaseDocument.Name, StringComparison.OrdinalIgnoreCase ) )
			return;

		_session.EnsureReleaseNotFinalized( entry.Release );

		// one rename straight from where the plan is, never through unreleased: a move cut short
		// leaves the folder in one release or the other, and 'reindex' realigns the rest with it
		MovePlan( entry, entry.Release, releaseDocument.Name, ActionDocument.From( context ) );
	}

	public string? GetJournalingStatement( JournalingSlot slot )
	{
		string path = _session.Paths.JournalingStatementFile( slot );

		// File.ReadAllText detects and strips a BOM; no migration is wanted for projects
		// created before this feature, so an unset slot is simply an absent file
		return File.Exists( path ) ? File.ReadAllText( path ) : null;
	}

	/// <summary>
	/// The file ends in exactly one newline, whatever the caller supplied: the files are committed to
	/// version control, and normalizing here keeps a round trip stable — piping
	/// 'journaling print' back into 'journaling set' would otherwise add a line every time.
	/// </summary>
	public void SetJournalingStatement( JournalingSlot slot, string statement )
		=> AtomicFile.WriteAllText(
			_session.Paths.JournalingStatementFile( slot ),
			statement.TrimEnd( '\r', '\n' ) + "\n"
		);

	public void Dispose()
	{
		// all writes are flushed eagerly; nothing to release
	}

	private void MovePlan( PlanIndexEntry entry, string fromRelease, string toRelease, ActionDocument? assignment )
	{
		string source = _session.Paths.PlanDirectory( fromRelease, entry.Name );
		string destination = _session.Paths.PlanDirectory( toRelease, entry.Name );

		Directory.CreateDirectory( Path.GetDirectoryName( destination )! );
		Directory.Move( source, destination );

		string planFile = Path.Combine( destination, FileNames.PlanFile );

		JsonFiles.Write(
			planFile,
			JsonFiles.Read<PlanDocument>( planFile ) with { Release = toRelease, ReleaseAssignment = assignment }
		);

		_session.PlanIndex.Set( entry with { Release = toRelease } );
	}

	private PlanDocument ReadPlanDocument( PlanIndexEntry entry )
		=> JsonFiles.Read<PlanDocument>( _session.Paths.PlanFile( entry.Release, entry.Name ) );

	private (PlanIndexEntry Entry, string PlanFile, PlanDocument Document) RequirePlanDocument( string name )
	{
		PlanIndexEntry entry = _session.RequirePlan( name );

		string planFile = _session.Paths.PlanFile( entry.Release, entry.Name );

		return ( entry, planFile, JsonFiles.Read<PlanDocument>( planFile ) );
	}

	private (PlanIndexEntry Entry, string StepFile, StepDocument Document) RequireStepDocument(
		string plan,
		string name
	)
	{
		PlanIndexEntry entry = _session.RequirePlan( plan );

		string? stepDirectory = FindStepDirectory( entry, name ) ??
								throw new ProjectStoreException( $"Step '{name}' of plan '{plan}' does not exist." );

		return ( entry, Path.Combine( stepDirectory, FileNames.StepFile ), ReadStepDocument( stepDirectory ) );
	}

	// a directory only counts as a step once step.json exists; scripts may be written
	// into the directory before the step itself is registered
	private string? FindStepDirectory( PlanIndexEntry entry, string step )
	{
		string stepsDirectory = _session.Paths.StepsDirectory( entry.Release, entry.Name );

		if( !Directory.Exists( stepsDirectory ) )
			return null;

		return Directory.EnumerateDirectories( stepsDirectory )
			.FirstOrDefault( x => string.Equals( Path.GetFileName( x ), step, StringComparison.OrdinalIgnoreCase ) &&
								File.Exists( Path.Combine( x, FileNames.StepFile ) )
			);
	}

	private IEnumerable<StepDocument> EnumerateStepDocuments( PlanIndexEntry entry )
		=> EnumerateSteps( entry ).Select( x => x.Document );

	private IEnumerable<(string Directory, StepDocument Document)> EnumerateSteps( PlanIndexEntry entry )
		=> StepFolders.Enumerate( _session.Paths.StepsDirectory( entry.Release, entry.Name ) );

	private static StepDocument ReadStepDocument( string stepDirectory )
		=> StepFolders.Named( JsonFiles.Read<StepDocument>( Path.Combine( stepDirectory, FileNames.StepFile ) ), stepDirectory );

	private IEnumerable<ReleaseDocument> EnumerateReleaseDocuments( string prefix )
	{
		foreach( string releaseDirectory in _session.Paths.EnumerateReleaseDirectories( prefix ) )
			yield return JsonFiles.Read<ReleaseDocument>( Path.Combine( releaseDirectory, FileNames.ReleaseFile ) );
	}

	private IEnumerable<ReleaseDocument> MatchReleaseDocuments( ReleaseSearchCriteria criteria )
	{
		// a finalize window or finalize user only makes sense against finalized releases
		bool finalized = criteria.Finalized ||
						criteria.FinalizeTimestamp is not null ||
						criteria.FinalizeUser is not null;

		foreach( ReleaseDocument document in EnumerateReleaseDocuments( criteria.Prefix ) )
		{
			if( finalized != document.Finalized is not null )
				continue;

			if( criteria.CreateTimestamp is not null &&
				!document.Registration.Timestamp.IsWithin( criteria.CreateTimestamp ) )
				continue;

			if( criteria.FinalizeTimestamp is not null &&
				!document.Finalized!.Timestamp.IsWithin( criteria.FinalizeTimestamp ) )
				continue;

			if( !MatchesUser( document.Registration, criteria.CreateUser ) )
				continue;

			if( !MatchesUser( document.Finalized, criteria.FinalizeUser ) )
				continue;

			yield return document;
		}
	}

	private IEnumerable<ProjectPlan> ListUnreleasedPlans( PlanSearchCriteria criteria )
	{
		List<ProjectPlan> plans = [ ];

		foreach( PlanIndexEntry entry in _session.PlanIndex.Entries.Where( x => x.Release.Length == 0 ) )
		{
			PlanDocument document = ReadPlanDocument( entry );

			if( MatchesUser( document.Registration, criteria.CreateUser ) )
				plans.Add( document.ToModel() );
		}

		return plans;
	}

	private IEnumerable<ProjectPlan> ListReleasePlans( string release, PlanSearchCriteria criteria )
	{
		ReleaseDocument document = _session.RequireRelease( release );

		return ListPlansOfRelease( document.Name, criteria );
	}

	/// <summary>
	/// Plans of every matching release, grouped by release with the groups in
	/// release-chronological (dependency-chain) order, so the business layer only has to
	/// order plans within each group.
	/// </summary>
	private IEnumerable<ProjectPlan> ListReleasedPlans( PlanSearchCriteria criteria )
	{
		List<ReleaseDocument> releases = [ ];

		if( criteria.ReleaseFinalizeTimestamp is null )
		{
			// membership in any release qualifies, open or finalized
			releases.AddRange( MatchReleaseDocuments( new ReleaseSearchCriteria() ) );
			releases.AddRange( MatchReleaseDocuments( new ReleaseSearchCriteria( Finalized: true ) ) );
		}
		else
			releases.AddRange(
				MatchReleaseDocuments(
					new ReleaseSearchCriteria( FinalizeTimestamp: criteria.ReleaseFinalizeTimestamp )
				)
			);

		List<ProjectPlan> plans = [ ];

		foreach( ReleaseDocument release in OrderReleasesChronologically( releases ) )
			plans.AddRange( ListPlansOfRelease( release.Name, criteria ) );

		return plans;
	}

	private List<ProjectPlan> ListPlansOfRelease( string release, PlanSearchCriteria criteria )
	{
		string plansDirectory = _session.Paths.ReleasePlansDirectory( release );

		List<ProjectPlan> plans = [ ];

		if( !Directory.Exists( plansDirectory ) )
			return plans;

		foreach( string planDirectory in Directory.EnumerateDirectories( plansDirectory ) )
		{
			PlanDocument? document =
				JsonFiles.TryRead<PlanDocument>( Path.Combine( planDirectory, FileNames.PlanFile ) );

			if( document is not null && MatchesUser( document.Registration, criteria.CreateUser ) )
				plans.Add( document.ToModel() );
		}

		return plans;
	}

	/// <summary>
	/// Orders releases by their dependency chain (a release comes after the release it depends
	/// on), breaking ties by registration timestamp then name. Dependencies outside the given
	/// set and cycles are tolerated: unsortable remainders fall back to timestamp order.
	/// </summary>
	private static IEnumerable<ReleaseDocument> OrderReleasesChronologically( List<ReleaseDocument> releases )
	{
		Dictionary<string, ReleaseDocument> byName = new( StringComparer.OrdinalIgnoreCase );

		foreach( ReleaseDocument release in releases )
			byName[release.Name] = release;

		List<ReleaseDocument> pending = releases
			.OrderBy( x => x.Registration.Timestamp )
			.ThenBy( x => x.Name, StringComparer.OrdinalIgnoreCase )
			.ToList();

		HashSet<string> emitted = new( StringComparer.OrdinalIgnoreCase );
		List<ReleaseDocument> ordered = [ ];

		while( pending.Count > 0 )
		{
			// emit the earliest-registered release whose in-set dependency was emitted, so
			// this grouping agrees with the business layer's release ordering semantics
			int ready = pending.FindIndex( x =>
				x.Dependency.Length == 0 || !byName.ContainsKey( x.Dependency ) || emitted.Contains( x.Dependency )
			);

			// dependency cycle: emit the remainder in timestamp order rather than failing
			if( ready < 0 )
			{
				ordered.AddRange( pending );

				break;
			}

			ordered.Add( pending[ready] );
			emitted.Add( pending[ready].Name );
			pending.RemoveAt( ready );
		}

		return ordered;
	}

	private static bool MatchesUser( ActionDocument? action, string? user )
		=> user is null || string.Equals( action?.Subject.EmailAddress, user, StringComparison.OrdinalIgnoreCase );
}