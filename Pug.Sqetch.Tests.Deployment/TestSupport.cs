using System.Data;
using System.Text;
using Pug.Sqetch.Bundling;
using Pug.Sqetch.Bundling.Layouts;
using Pug.Sqetch.Deployment;
using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Models;

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
	public List<string> Events { get; } = [];

	/// <summary>
	/// Step scripts of committed transactions, in the order they ran — what the database kept.
	/// </summary>
	public List<string> AppliedScripts { get; } = [];

	/// <summary>Scripts whose execution should fail.</summary>
	public HashSet<string> FailingScripts { get; } = [];

	/// <summary>Journaling statements whose execution should fail, by exact statement text.</summary>
	public HashSet<string> FailingJournalingStatements { get; } = [];

	/// <summary>Journaling queries whose execution should fail, by exact query text.</summary>
	public HashSet<string> FailingJournalingQueries { get; } = [];

	public List<FakeTransaction> Transactions { get; } = [];

	/// <summary>
	/// Canned result sets for <see cref="FakeTransaction.ExecuteJournalingQuery"/>, keyed by the exact
	/// query text. A test seeds one per query slot's SQL before exercising <c>StatementJournal</c>.
	/// </summary>
	public Dictionary<string, DataTable> QueryResults { get; } = new ();

	/// <summary>
	/// Interprets the marker journaling statements a deployment runs, so the durable journal state
	/// can be asserted. Attached by <see cref="FakeJournal.Attach"/>; null when a test drives
	/// journaling statements directly and does not care about their meaning.
	/// </summary>
	public FakeJournal? Journal { get; set; }

	/// <summary>Whether the host released the driver, as a real one's connection would need.</summary>
	public bool Disposed { get; private set; }

	public IDatabaseTransaction BeginTransaction()
	{
		FakeTransaction transaction = new ( this, Transactions.Count + 1 );

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
	private readonly List<Action> _onCommit = [];

	/// <summary>The driver this transaction was begun on — a test seeds canned query results here.</summary>
	public FakeDatabaseDriver Driver { get; } = driver;

	public int Number { get; } = number;

	public bool Committed { get; private set; }

	public bool RolledBack { get; private set; }

	/// <summary>
	/// The journal entries written in this transaction, so a test can assert what became durable
	/// together — notably that a release's completion shares its last plan's transaction.
	/// </summary>
	public List<string> Journaled { get; } = [];

	/// <summary>
	/// Every journaling statement and query this transaction executed, paired with the parameters it
	/// was called with — so a test can assert exact <c>(statement, parameters)</c> pairs without
	/// parsing <see cref="FakeDatabaseDriver.Events"/> strings.
	/// </summary>
	public List<(string Statement, IReadOnlyList<JournalingParameter> Parameters)> JournalingCalls { get; } = [];

	/// <summary>
	/// Every reader <see cref="ExecuteJournalingQuery"/> handed out, in order — so a test can assert
	/// the caller disposed it.
	/// </summary>
	public List<IDataReader> QueryReaders { get; } = [];

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
		JournalingCalls.Add( (statement, parameters) );
		driver.Events.Add( $"journal-statement #{Number} {statement} [{Format( parameters )}]" );

		if( driver.FailingJournalingStatements.Contains( statement ) )
			throw new InvalidOperationException( $"journaling statement rejected: {statement}" );

		driver.Journal?.Observe( statement, parameters, this );
	}

	public IDataReader ExecuteJournalingQuery( string query, IReadOnlyList<JournalingParameter> parameters )
	{
		JournalingCalls.Add( (query, parameters) );
		driver.Events.Add( $"journal-query #{Number} {query} [{Format( parameters )}]" );

		if( driver.FailingJournalingQueries.Contains( query ) )
			throw new InvalidOperationException( $"journaling query rejected: {query}" );

		if( !driver.QueryResults.TryGetValue( query, out DataTable? table ) )
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
			parameters.Select(
				p => $"{p.Name}={( p.Value is DateTime time ? $"<{time.Kind.ToString().ToLowerInvariant()}>" : p.Value )}" ) );

	public void Rollback()
	{
		RolledBack = true;

		// nothing this transaction wrote survives, journal entries included
		_onCommit.Clear();

		driver.Events.Add( $"rollback #{Number}" );
	}

	public void Commit()
	{
		Committed = true;

		foreach( Action apply in _onCommit )
			apply();

		_onCommit.Clear();

		driver.Events.Add( $"commit #{Number}" );
	}
}

public sealed class RecordingListener : IDeploymentListener
{
	public List<string> Events { get; } = [];

	public void ContinuingFrom( string release ) => Events.Add( $"continuing {release}" );

	public void SkippingRelease( string release ) => Events.Add( $"skip-release {release}" );

	public void SkippingPlan( string release, string plan ) => Events.Add( $"skip-plan {release}/{plan}" );

	public void DeployingRelease( string release ) => Events.Add( $"release {release}" );

	public void DeployingPlan( string release, string plan ) => Events.Add( $"plan {release}/{plan}" );

	public void DeployingStep( string release, string plan, string step ) => Events.Add( $"step {release}/{plan}/{step}" );

	public void StepDeployed( string release, string plan, string step ) => Events.Add( $"step-done {release}/{plan}/{step}" );

	public void PlanDeployed( string release, string plan ) => Events.Add( $"plan-done {release}/{plan}" );

	public void ReleaseDeployed( string release ) => Events.Add( $"release-done {release}" );

	public void Committed() => Events.Add( "committed" );

	public void NothingToDeploy() => Events.Add( "nothing" );

	public void RollingBack( string reason ) => Events.Add( $"rolling-back {reason}" );

	public void RollingBackPlan( string release, string plan ) => Events.Add( $"rollback-plan {release}/{plan}" );

	public void RolledBack() => Events.Add( "rolled-back" );
}

public sealed class InMemoryBundleReader : IBundleReader
{
	private readonly Dictionary<string, string> _entries = new ();

	public InMemoryBundleReader Add( string path, string content )
	{
		_entries[path] = content;

		return this;
	}

	public InMemoryBundleReader Remove( string path )
	{
		_entries.Remove( path );

		return this;
	}

	public IEnumerable<string> Entries => _entries.Keys;

	public bool Contains( string path ) => _entries.ContainsKey( path );

	public Stream Open( string path )
		=> _entries.TryGetValue( path, out string? content )
			? new MemoryStream( Encoding.UTF8.GetBytes( content ) )
			: throw new BundlingException( $"Bundle has no entry '{path}'." );

	public void Dispose()
	{
	}
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

	public Dictionary<string, List<string>> DeployedPlans { get; } = [];

	/// <summary>
	/// Releases whose <em>start</em> was journaled, in order. A release deployment resumes into is
	/// absent: the run that began it recorded that already.
	/// </summary>
	public List<string> StartedReleases { get; } = [];

	public static string Marker( JournalingSlot slot ) => $"{Prefix}{slot}";

	/// <summary>Marker SQL for every slot — what a test bundles.</summary>
	public static JournalingStatements Statements()
		=> new ( JournalingSlots.All.ToDictionary( slot => slot, slot => (string?)Marker( slot ) ) );

	/// <summary>
	/// Answers the two query slots from the seeded state and starts interpreting the statements the
	/// deployment runs. Call once, before deploying.
	/// </summary>
	public void Attach( FakeDatabaseDriver driver )
	{
		driver.Journal = this;

		DataTable latest = new ();

		latest.Columns.Add( "release", typeof(string) );
		latest.Columns.Add( "completed", typeof(bool) );

		if( Latest is not null )
			latest.Rows.Add( Latest.Name, Latest.Completed );

		driver.QueryResults[Marker( JournalingSlot.GetLatestRelease )] = latest;

		DataTable plans = new ();

		plans.Columns.Add( "plan", typeof(string) );

		// only a release that is incompletely deployed is ever asked about, so the seeded plans of
		// the latest release are the only ones the engine can request
		if( Latest is not null && DeployedPlans.TryGetValue( Latest.Name, out List<string>? held ) )
			foreach( string plan in held )
				plans.Rows.Add( plan );

		driver.QueryResults[Marker( JournalingSlot.GetDeployedPlans )] = plans;
	}

	internal void Observe(
		string statement, IReadOnlyList<JournalingParameter> parameters, FakeTransaction transaction )
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
			JournalingSlots.TryParse( slot, out JournalingSlot parsed ) ? JournalingSlots.ParameterNames( parsed ) : [];

		transaction.Journaled.Add(
			carries.Contains( JournalingSlots.Parameters.Step ) ? $"{slot} {release}/{plan}/{step}"
			: carries.Contains( JournalingSlots.Parameters.Plan ) ? $"{slot} {release}/{plan}"
			: carries.Contains( JournalingSlots.Parameters.Release ) ? $"{slot} {release}"
			: slot );

		switch( slot )
		{
			case nameof(JournalingSlot.DeployingRelease):
				transaction.OnCommit(
					() =>
					{
						StartedReleases.Add( release );
						Latest = new JournaledRelease( release, Completed: false );
					} );

				break;

			case nameof(JournalingSlot.ReleaseDeployed):
				transaction.OnCommit( () => Latest = new JournaledRelease( release, Completed: true ) );

				break;

			case nameof(JournalingSlot.PlanDeployed):
				transaction.OnCommit( () => Plans( release ).Add( plan ) );

				break;
		}
	}

	private List<string> Plans( string release )
		=> DeployedPlans.TryGetValue( release, out List<string>? plans ) ? plans : DeployedPlans[release] = [];
}

public static class Manifests
{
	public static BundleManifestStep Step( string name ) => new ( name, "", [] );

	public static BundleManifestPlan Plan( string name, string release, params string[] steps )
		=> new ( name, "", release, [], steps.Select( Step ).ToList() );

	public static BundleManifestRelease Release( string name, string dependency = "" )
		=> new ( name, "", dependency, true );

	public static BundleManifest Manifest(
		IReadOnlyList<BundleManifestRelease> releases, IReadOnlyList<BundleManifestPlan> plans )
		=> new ( new BundleManifestProject( "demo", "", "postgres" ), BundleSelection.Finalized,
				releases, plans );

	/// <summary>Journaling SQL for every slot; see <see cref="FakeJournal.Statements"/>.</summary>
	public static JournalingStatements Journaling() => FakeJournal.Statements();

	/// <summary>The instant the in-memory bundles claim to have been built at.</summary>
	public static DateTime Bundled { get; } = new ( 2026, 10, 17, 3, 15, 42, DateTimeKind.Utc );

	/// <summary>The metadata a bundle written through <paramref name="layout"/> would carry.</summary>
	public static BundleMetadata Metadata( IBundleLayout layout )
		=> new ( layout.Format, layout.Name, "sqetch-deploy", "0.1.0", Bundled );

	/// <summary>A registry holding the built-in layouts, as the deployment host's does.</summary>
	public static BundleLayoutRegistry Layouts()
	{
		BundleLayoutRegistry layouts = new ();

		layouts.RegisterLayouts();

		return layouts;
	}

	/// <summary>
	/// A reader holding the metadata and all three scripts for every step of the manifest, plus the
	/// journaling SQL — everything but the manifest itself, which a test adds in the shape it needs.
	/// </summary>
	public static InMemoryBundleReader ReaderFor( BundleManifest manifest, IBundleLayout layout )
	{
		InMemoryBundleReader reader = new ();

		reader.Add( BundleMetadata.EntryName, Metadata( layout ).ToText() );

		foreach( BundleManifestPlan plan in manifest.Plans )
			foreach( BundleManifestStep step in plan.Steps )
				foreach( StepScriptKind kind in Enum.GetValues<StepScriptKind>() )
					reader.Add(
						layout.ScriptPath( plan.Name, step.Name, kind ),
						$"-- {kind.ToString().ToLowerInvariant()} {plan.Name}/{step.Name}" );

		foreach( JournalingSlot slot in JournalingSlots.All )
			reader.Add( $"journaling/{JournalingSlots.FileName( slot )}", FakeJournal.Marker( slot ) );

		return reader;
	}
}
