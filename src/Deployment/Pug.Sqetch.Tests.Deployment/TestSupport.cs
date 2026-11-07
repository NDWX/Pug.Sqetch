using System.Data;
using System.Text;

using Pug.Sqetch.Deployment;
using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Tests.Deployment;

/// <summary>
/// Fakes for the deployment tier. They model two different things, and a test should know which
/// one it is asserting:
/// <list type="bullet">
/// <item><description>the <em>result</em> of a deployment — the journal state a later run would
/// read back (<see cref="FakeJournal"/>) and the step scripts the database actually kept
/// (<see cref="FakeDatabaseDriver.AppliedScripts"/>). Both become visible only when the
/// transaction that produced them commits, as a real database's would.</description></item>
/// <item><description>how it got there — <see cref="FakeDatabaseDriver.Events"/> for the exact
/// interleaving of scripts, journal entries and transaction boundaries, and
/// <see cref="FakeTransaction.Journaled"/> for what one transaction made durable
/// together.</description></item>
/// </list>
/// <see cref="RecordingListener"/> is neither: it is the host's progress channel.
/// </summary>
public sealed class FakeDatabaseDriver : IDatabaseDriver
{
	/// <summary>
	/// Every driver, transaction and journal call in the order it was made, committed or not.
	/// Assert against this only where the interleaving or the transaction boundaries are the
	/// subject of the test.
	/// </summary>
	public List<string> Events { get; } = [ ];

	/// <summary>
	/// Step scripts of committed transactions, in the order they ran — what the database kept.
	/// </summary>
	public List<string> AppliedScripts { get; } = [ ];

	/// <summary>Scripts whose execution should fail.</summary>
	public HashSet<string> FailingScripts { get; } = [ ];

	/// <summary>
	/// Makes every rollback throw after taking effect, as a provider does for a transaction the server
	/// already ended or over a broken connection.
	/// </summary>
	public bool FailingRollback { get; set; }

	/// <summary>Numbers of the transactions whose commit should throw, leaving them uncommitted.</summary>
	public HashSet<int> FailingCommits { get; } = [ ];

	/// <summary>Journaling statements whose execution should fail, by exact statement text.</summary>
	public HashSet<string> FailingJournalingStatements { get; } = [ ];

	/// <summary>Journaling queries whose execution should fail, by exact query text.</summary>
	public HashSet<string> FailingJournalingQueries { get; } = [ ];

	public List<FakeTransaction> Transactions { get; } = [ ];

	/// <summary>
	/// Canned result sets for <see cref="FakeTransaction.ExecuteJournalingQuery"/>, keyed by the exact
	/// query text. A test seeds one per query slot's SQL before exercising <c>StatementJournal</c>.
	/// </summary>
	public Dictionary<string, DataTable> QueryResults { get; } = new();

	/// <summary>
	/// Result sets computed from the bound parameters, keyed by the exact query text, consulted before
	/// <see cref="QueryResults"/>. For a query whose answer depends on what it is asked, such as the
	/// plans of one release.
	/// </summary>
	public Dictionary<string, Func<IReadOnlyList<JournalingParameter>, DataTable>> QueryResultFactories { get; } = new();

	/// <summary>
	/// Interprets the marker journaling statements a deployment runs, so the durable journal state
	/// can be asserted. Attached by <see cref="FakeJournal.Attach"/>; null when a test drives
	/// journaling statements directly and does not care about their meaning.
	/// </summary>
	public FakeJournal? Journal { get; set; }

	/// <summary>Whether the host released the driver, as a real one's connection would need.</summary>
	public bool Disposed { get; private set; }

	/// <summary>Poses as an engine that commits DDL by itself, as MySQL and MariaDB do.</summary>
	public bool CommitsDdlImplicitly { get; set; }

	public IDatabaseTransaction BeginTransaction()
	{
		FakeTransaction transaction = new( this, Transactions.Count + 1 );

		Transactions.Add( transaction );
		Events.Add( $"begin #{transaction.Number}" );

		return transaction;
	}

	public void Dispose()
	{
		Disposed = true;

		Events.Add( "dispose" );
	}
}

public sealed class FakeTransaction( FakeDatabaseDriver driver, int number ) : IDatabaseTransaction
{
	private readonly List<Action> _onCommit = [ ];

	/// <summary>The driver this transaction was begun on — a test seeds canned query results here.</summary>
	public FakeDatabaseDriver Driver { get; } = driver;

	public int Number { get; } = number;

	public bool Committed { get; private set; }

	public bool RolledBack { get; private set; }

	/// <summary>
	/// The journal entries written in this transaction, so a test can assert what became durable
	/// together — notably that a release's completion shares its last plan's transaction.
	/// </summary>
	public List<string> Journaled { get; } = [ ];

	/// <summary>
	/// Every journaling statement and query this transaction executed, paired with the parameters it
	/// was called with — so a test can assert exact <c>(statement, parameters)</c> pairs without
	/// parsing <see cref="FakeDatabaseDriver.Events"/> strings.
	/// </summary>
	public List<(string Statement, IReadOnlyList<JournalingParameter> Parameters)> JournalingCalls { get; } = [ ];

	/// <summary>
	/// Every reader <see cref="ExecuteJournalingQuery"/> handed out, in order — so a test can assert
	/// the caller disposed it.
	/// </summary>
	public List<IDataReader> QueryReaders { get; } = [ ];

	/// <summary>Registers state this transaction makes durable when, and only when, it commits.</summary>
	public void OnCommit( Action apply ) => _onCommit.Add( apply );

	public void ExecuteStepScript( string script )
	{
		if( driver.FailingScripts.Contains( script ) )
			throw new InvalidOperationException( $"script rejected: {script}" );

		driver.Events.Add( $"script #{Number} {script}" );
		OnCommit( () => driver.AppliedScripts.Add( script ) );
	}

	public void ExecuteJournalingStatement( string statement, IReadOnlyList<JournalingParameter> parameters )
	{
		JournalingCalls.Add( ( statement, parameters ) );
		driver.Events.Add( $"journal-statement #{Number} {statement} [{Format( parameters )}]" );

		if( driver.FailingJournalingStatements.Contains( statement ) )
			throw new InvalidOperationException( $"journaling statement rejected: {statement}" );

		driver.Journal?.Observe( statement, parameters, this );
	}

	public IDataReader ExecuteJournalingQuery( string query, IReadOnlyList<JournalingParameter> parameters )
	{
		JournalingCalls.Add( ( query, parameters ) );
		driver.Events.Add( $"journal-query #{Number} {query} [{Format( parameters )}]" );

		if( driver.FailingJournalingQueries.Contains( query ) )
			throw new InvalidOperationException( $"journaling query rejected: {query}" );

		DataTable? table;

		if( driver.QueryResultFactories.TryGetValue( query, out Func<IReadOnlyList<JournalingParameter>, DataTable>? factory ) )
			table = factory( parameters );
		else
			driver.QueryResults.TryGetValue( query, out table );

		if( table is null )
			throw new InvalidOperationException( $"no canned result seeded for query: {query}" );

		IDataReader reader = table.CreateDataReader();

		QueryReaders.Add( reader );

		return reader;
	}

	/// <summary>
	/// The event line's parameter list. A <see cref="DateTime"/> renders as its kind rather than its
	/// value — the instant is whatever the clock said, so no sequence assertion could spell it out —
	/// which still pins that the parameter arrived as a UTC <see cref="DateTime"/> and not as text.
	/// <c>StatementJournalTests</c> asserts the instant itself, against a fixed clock.
	/// </summary>
	private static string Format( IReadOnlyList<JournalingParameter> parameters )
		=> string.Join(
			", ",
			parameters.Select( p =>
				$"{p.Name}={( p.Value is DateTime time ? $"<{time.Kind.ToString().ToLowerInvariant()}>" : p.Value )}"
			)
		);

	public void Rollback()
	{
		RolledBack = true;

		// nothing this transaction wrote survives, journal entries included
		_onCommit.Clear();

		driver.Events.Add( $"rollback #{Number}" );

		if( driver.FailingRollback )
			throw new InvalidOperationException( "rollback failed" );
	}

	public void Commit()
	{
		if( driver.FailingCommits.Contains( Number ) )
		{
			driver.Events.Add( $"commit-failed #{Number}" );

			throw new InvalidOperationException( "commit failed" );
		}

		Committed = true;

		foreach( Action apply in _onCommit )
			apply();

		_onCommit.Clear();

		driver.Events.Add( $"commit #{Number}" );
	}
}

public sealed class RecordingListener : IDeploymentListener
{
	public List<string> Events { get; } = [ ];

	public void ContinuingFrom( string release ) => Events.Add( $"continuing {release}" );

	public void SkippingRelease( string release ) => Events.Add( $"skip-release {release}" );

	public void SkippingPlan( string release, string plan ) => Events.Add( $"skip-plan {release}/{plan}" );

	public void DeployingRelease( string release ) => Events.Add( $"release {release}" );

	public void DeployingPlan( string release, string plan ) => Events.Add( $"plan {release}/{plan}" );

	public void DeployingStep( string release, string plan, string step ) =>
		Events.Add( $"step {release}/{plan}/{step}" );

	public void VerifyingStep( string release, string plan, string step ) =>
		Events.Add( $"verifying {release}/{plan}/{step}" );

	public void StepDeployed( string release, string plan, string step ) =>
		Events.Add( $"step-done {release}/{plan}/{step}" );

	public void PlanDeployed( string release, string plan ) => Events.Add( $"plan-done {release}/{plan}" );

	public void ReleaseDeployed( string release ) => Events.Add( $"release-done {release}" );

	public void Committed() => Events.Add( "committed" );

	public void NothingToDeploy() => Events.Add( "nothing" );

	public void RollingBack( string reason ) => Events.Add( $"rolling-back {reason}" );

	public void RollingBackPlan( string release, string plan ) => Events.Add( $"rollback-plan {release}/{plan}" );

	public void RollingBackRelease( string release ) => Events.Add( $"rollback-release {release}" );

	public void NothingToRollBack() => Events.Add( "nothing-to-roll-back" );

	public void RolledBack() => Events.Add( "rolled-back" );

	/// <summary>Makes <see cref="LockAcquired"/> throw, as a misbehaving host listener could.</summary>
	public bool FailingLockAcquired { get; set; }

	public void LockAcquired()
	{
		Events.Add( "lock-acquired" );

		if( FailingLockAcquired )
			throw new InvalidOperationException( "listener failed" );
	}

	public void LockNotReleased( Exception exception ) => Events.Add( $"lock-not-released {exception.Message}" );
}

/// <summary>
/// Stands in for a journal now that there is no journal-writer abstraction to fake. A deployment
/// journals by running the project's SQL, so a test bundles marker statements —
/// "journal &lt;slot&gt;" — and this interprets them: the statement text names the slot and the bound
/// parameters carry the release, plan and step.
///
/// <see cref="Latest"/> and <see cref="DeployedPlans"/> are the seed, answering the two query slots
/// as an earlier run would have left them; they then accumulate what this run journaled, applied on
/// commit so a rolled-back transaction leaves no trace. Assert them to assert the result.
/// </summary>
public sealed class FakeJournal
{
	private const string Prefix = "journal ";

	public JournaledRelease? Latest { get; set; }

	/// <summary>What the optional JournalExists query answers; false poses as a database never deployed to.</summary>
	public bool JournalExists { get; set; } = true;

	public Dictionary<string, List<string>> DeployedPlans { get; } = [ ];

	/// <summary>
	/// Releases whose <em>start</em> was journaled, in order. A release deployment resumes into is
	/// absent: the run that began it recorded that already.
	/// </summary>
	public List<string> StartedReleases { get; } = [ ];

	public static string Marker( JournalingSlot slot ) => $"{Prefix}{slot}";

	/// <summary>
	/// What the optional AcquireLock query answers: true poses as a lock that was free, false as one
	/// another deployment holds. Read by <see cref="Attach"/>.
	/// </summary>
	public bool LockAvailable { get; set; } = true;

	/// <summary>
	/// Marker SQL for every slot but the lock pair — what a test bundles. The lock is opt-in
	/// (<paramref name="withLock"/>) because setting it changes the transactions a deployment opens.
	/// </summary>
	public static JournalingStatements Statements( bool withLock = false )
		=> new(
			JournalingSlots.All
				.Where( slot => withLock || slot is not (JournalingSlot.AcquireLock or JournalingSlot.ReleaseLock) )
				.ToDictionary( slot => slot, slot => (string?)Marker( slot ) )
		);

	/// <summary>Marker SQL for the required slots alone — a bundle made without the optional JournalExists.</summary>
	public static JournalingStatements RequiredStatements()
		=> new( JournalingSlots.Required.ToDictionary( slot => slot, slot => (string?)Marker( slot ) ) );

	/// <summary>
	/// Answers the two query slots from the seeded state and starts interpreting the statements the
	/// deployment runs. Call once, before deploying.
	/// </summary>
	public void Attach( FakeDatabaseDriver driver )
	{
		driver.Journal = this;

		DataTable exists = new();

		exists.Columns.Add( "exists", typeof(bool) );
		exists.Rows.Add( JournalExists );

		driver.QueryResults[Marker( JournalingSlot.JournalExists )] = exists;

		DataTable acquired = new();

		acquired.Columns.Add( "acquired", typeof(bool) );
		acquired.Rows.Add( LockAvailable );

		driver.QueryResults[Marker( JournalingSlot.AcquireLock )] = acquired;

		// from the live state, so a rollback after a deployment through the same journal reads it back
		driver.QueryResultFactories[Marker( JournalingSlot.GetLatestRelease )] = _ => {
			DataTable latest = new();

			latest.Columns.Add( "release", typeof(string) );
			latest.Columns.Add( "completed", typeof(bool) );

			if( Latest is not null )
				latest.Rows.Add( Latest.Name, Latest.Completed );

			return latest;
		};

		// answered by the release asked about, from the live state: a rollback asks for several
		driver.QueryResultFactories[Marker( JournalingSlot.GetDeployedPlans )] = parameters => {
			string release = parameters.Single( p => p.Name == JournalingSlots.Parameters.Release ).Value.ToString() ?? "";

			DataTable plans = new();

			plans.Columns.Add( "plan", typeof(string) );

			if( DeployedPlans.TryGetValue( release, out List<string>? held ) )
				foreach( string plan in held )
					plans.Rows.Add( plan );

			return plans;
		};
	}

	internal void Observe(
		string statement,
		IReadOnlyList<JournalingParameter> parameters,
		FakeTransaction transaction
	)
	{
		if( !statement.StartsWith( Prefix, StringComparison.Ordinal ) )
			return;

		string Value( string name )
			=> parameters.SingleOrDefault( parameter => parameter.Name == name ).Value?.ToString() ?? "";

		string release = Value( JournalingSlots.Parameters.Release );
		string plan = Value( JournalingSlots.Parameters.Plan );
		string step = Value( JournalingSlots.Parameters.Step );
		string slot = statement[Prefix.Length..];

		// the entry shows exactly the identity the slot carries, so a slot with no release — the
		// journal's own schema — is not recorded as one with a blank release
		IReadOnlyList<string> carries =
			JournalingSlots.TryParse( slot, out JournalingSlot parsed ) ?
				JournalingSlots.ParameterNames( parsed ) : [ ];

		transaction.Journaled.Add(
			carries.Contains( JournalingSlots.Parameters.Step ) ? $"{slot} {release}/{plan}/{step}"
			: carries.Contains( JournalingSlots.Parameters.Plan ) ? $"{slot} {release}/{plan}"
			: carries.Contains( JournalingSlots.Parameters.Release ) ? $"{slot} {release}"
			: slot
		);

		switch( slot )
		{
			case nameof(JournalingSlot.DeployingRelease):
				transaction.OnCommit( () => {
						StartedReleases.Add( release );
						Latest = new JournaledRelease( release, Completed: false );
					}
				);

			break;

			case nameof(JournalingSlot.ReleaseDeployed):
				transaction.OnCommit( () => Latest = new JournaledRelease( release, Completed: true ) );

			break;

			// the templates' completed rule: ReleaseDeployed counts only after the latest RollingBackRelease,
			// so a rollback cut short inside the latest release leaves it incomplete. A rollback works newest
			// first, so the release being undone is the template's latest (the newer ones are rolled back)
			case nameof(JournalingSlot.RollingBackRelease):
				transaction.OnCommit( () => Latest = new JournaledRelease( release, Completed: false ) );

			break;

			case nameof(JournalingSlot.PlanDeployed):
				transaction.OnCommit( () => Plans( release ).Add( plan ) );

			break;

			case nameof(JournalingSlot.RolledBackPlan):
				transaction.OnCommit( () => Plans( release ).Remove( plan ) );

			break;
		}
	}

	private List<string> Plans( string release )
		=> DeployedPlans.TryGetValue( release, out List<string>? plans ) ? plans : DeployedPlans[release] = [ ];
}

/// <summary>
/// Step scripts for engine tests, without a bundle: every step has a marker deploy and rollback
/// script ("-- deploy plan/step", "-- rollback plan/step") unless a test replaces one. Verify scripts
/// are blank, which the engine skips, unless a test sets one, so only verification tests see them run.
/// </summary>
public sealed class InMemoryScripts : IScriptContentReader
{
	private readonly Dictionary<(string Plan, string Step, bool Rollback), string> _scripts = new();

	private readonly Dictionary<(string Plan, string Step), string> _verify = new();

	private readonly Func<string, string, string> _deploy;

	private readonly Func<string, string, string> _rollback;

	private readonly Func<string, string, string> _defaultVerify;

	public InMemoryScripts(
		Func<string, string, string>? deploy = null,
		Func<string, string, string>? rollback = null,
		Func<string, string, string>? verify = null
	)
	{
		_defaultVerify = verify ?? ( ( _, _ ) => "" );
		_deploy = deploy ?? ( ( plan, step ) => $"-- deploy {plan}/{step}" );
		_rollback = rollback ?? ( ( plan, step ) => $"-- rollback {plan}/{step}" );
	}

	/// <summary>The releases asked about, one entry per deploy or rollback script read, in order.</summary>
	public List<string> Releases { get; } = [ ];

	public InMemoryScripts Deploy( string plan, string step, string script )
	{
		_scripts[( plan, step, false )] = script;

		return this;
	}

	public InMemoryScripts Verify( string plan, string step, string script )
	{
		_verify[( plan, step )] = script;

		return this;
	}

	public InMemoryScripts Rollback( string plan, string step, string script )
	{
		_scripts[( plan, step, true )] = script;

		return this;
	}

	public string ReadDeployScript( string release, string plan, string step )
	{
		Releases.Add( release );

		return _scripts.TryGetValue( ( plan, step, false ), out string? script ) ? script : _deploy( plan, step );
	}

	public string ReadVerifyScript( string release, string plan, string step )
		=> _verify.TryGetValue( ( plan, step ), out string? script ) ? script : _defaultVerify( plan, step );

	public string ReadRollbackScript( string release, string plan, string step )
	{
		Releases.Add( release );

		return _scripts.TryGetValue( ( plan, step, true ), out string? script ) ? script : _rollback( plan, step );
	}
}

/// <summary>
/// Release groups for engine tests, written the way a manifest lists them — releases, then plans
/// naming their release — so a test reads as the lineage it describes. Plans with an empty release
/// become a trailing pseudo-release depending on the last real one. No lineage rule is checked here:
/// that is the engine's job, and some tests hand it a broken lineage on purpose.
/// </summary>
public static class Lineages
{
	public sealed record ReleaseEntry( string Name, string Dependency );

	public sealed record PlanEntry( string Release, DeployablePlan Plan );

	public static ReleaseEntry Release( string name, string dependency = "" ) => new( name, dependency );

	public static PlanEntry Plan( string name, string release, params string[] steps )
		=> new(
			release,
			new DeployablePlan( name, "", steps.Select( step => new DeployableStep( step, "" ) ).ToList() )
		);

	public static IReadOnlyList<ReleaseGroup> Lineage(
		IReadOnlyList<ReleaseEntry> releases,
		IReadOnlyList<PlanEntry> plans
	)
	{
		List<ReleaseGroup> groups = releases
			.Select( release => new ReleaseGroup(
					release.Name,
					"",
					release.Dependency,
					plans.Where( plan => plan.Release == release.Name )
						.Select( plan => plan.Plan )
						.ToList()
				)
			)
			.ToList();

		List<DeployablePlan> unreleased =
			plans.Where( plan => plan.Release.Length == 0 ).Select( plan => plan.Plan ).ToList();

		if( unreleased.Count > 0 )
			groups.Add( new ReleaseGroup( "", "", releases.Count > 0 ? releases[^1].Name : "", unreleased ) );

		return groups;
	}

	/// <summary>The project name the engine tests deploy as.</summary>
	public const string Project = "demo";
}