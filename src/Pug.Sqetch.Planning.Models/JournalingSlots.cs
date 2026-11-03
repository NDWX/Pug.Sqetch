namespace Pug.Sqetch.Planning.Models;

/// <summary>
/// What each <see cref="JournalingSlot"/> is called on disk and which parameters its statements may
/// use. Both the project info store and the bundle layout resolve file names through
/// <see cref="FileName"/>, so the two never drift apart.
/// </summary>
public static class JournalingSlots
{
	/// <summary>Bare parameter names — no provider prefix; the maintainer writes '@release'.</summary>
	public static class Parameters
	{
		public const string Project = "project";

		public const string Release = "release";

		public const string Plan = "plan";

		public const string Step = "step";

		public const string Description = "description";

		/// <summary>
		/// When the deployment host observed the event, in UTC. For the maintainer whose engine
		/// cannot supply the instant itself — no usable 'now()', no column default, or a session
		/// time zone nobody can pin down — rather than as the preferred source: a server-side
		/// 'now()' cannot be disordered by a skewed deployment host. Bound as a
		/// <see cref="DateTime"/> with <see cref="DateTimeKind.Utc"/>, not as text, so the driver
		/// binds a timestamp and no statement has to cast or parse one.
		/// </summary>
		public const string UtcTimestamp = "utcTimestamp";
	}

	/// <summary>Every slot, in declaration order.</summary>
	public static IReadOnlyList<JournalingSlot> All { get; } = Enum.GetValues<JournalingSlot>();

	public static IReadOnlyList<string> Names { get; } = All.Select( slot => slot.ToString() ).ToList();

	/// <summary>
	/// The slots a project may leave unset: bundling and deploying never need them, and a command
	/// that does refuses a bundle lacking them. Optional so that projects and templates written before
	/// a slot existed stay complete, and so older tools, which never look for its file, are unaffected.
	/// </summary>
	public static bool IsOptional( JournalingSlot slot )
		=> slot is JournalingSlot.JournalExists or JournalingSlot.AcquireLock or JournalingSlot.ReleaseLock;

	/// <summary>Every slot a project must set before it can be bundled, in declaration order.</summary>
	public static IReadOnlyList<JournalingSlot> Required { get; } = All.Where( slot => !IsOptional( slot ) ).ToList();

	/// <summary>
	/// The slots that return a result set. A query slot holds exactly one statement — a result set
	/// has to come from somewhere unambiguous.
	/// </summary>
	public static bool IsQuery( JournalingSlot slot )
		=> slot is JournalingSlot.GetLatestRelease or JournalingSlot.GetDeployedPlans or JournalingSlot.JournalExists
			or JournalingSlot.AcquireLock;

	/// <summary>
	/// The twelve slots a deployment event fires — deploying and rolling back releases, plans and
	/// steps — as opposed to the queries, <see cref="JournalingSlot.PrepareJournal"/> and the lock.
	/// </summary>
	public static bool IsEvent( JournalingSlot slot )
		=> slot is >= JournalingSlot.DeployingRelease and <= JournalingSlot.RolledBackRelease;

	/// <summary>
	/// The slot's file name in the project's 'journaling' folder and in a bundle. Event slots take an
	/// 'On' prefix so they read as what they respond to; the others are not events and take none.
	/// </summary>
	public static string FileName( JournalingSlot slot ) => IsEvent( slot ) ? $"On{slot}.sql" : $"{slot}.sql";

	/// <summary>
	/// The slots that record something in the journal — the events — as opposed to reading it
	/// (<see cref="IsQuery"/>), migrating its schema (<see cref="JournalingSlot.PrepareJournal"/>) or
	/// locking. Only these are given <see cref="Parameters.UtcTimestamp"/>: a query filters on
	/// identity alone, prepare is DDL that records no event, and a lock records nothing.
	/// </summary>
	public static bool Writes( JournalingSlot slot ) => IsEvent( slot );

	/// <summary>
	/// The parameters supplied to the slot's statements. Every slot gets
	/// <see cref="Parameters.Project"/>: the SQL is frozen into the bundle, so a journal shared by
	/// two projects could not otherwise tell them apart. Every writing slot also gets
	/// <see cref="Parameters.UtcTimestamp"/>, last, for the engine that cannot supply the instant
	/// itself; a maintainer whose engine can is better served by 'now()', which no host clock can
	/// skew. Unused parameters are the normal case — a statement binds what it names — so carrying it
	/// on every writing slot costs nothing. There is deliberately no actor parameter; the maintainer
	/// uses 'current_user'.
	/// </summary>
	public static IReadOnlyList<string> ParameterNames( JournalingSlot slot )
		=> slot switch
		{
			JournalingSlot.PrepareJournal or JournalingSlot.GetLatestRelease or JournalingSlot.JournalExists
				or JournalingSlot.AcquireLock or JournalingSlot.ReleaseLock =>
				[
					Parameters.Project
				],

			JournalingSlot.GetDeployedPlans => [ Parameters.Project, Parameters.Release ],

			JournalingSlot.RollingBackRelease or JournalingSlot.RolledBackRelease =>
			[
				Parameters.Project, Parameters.Release, Parameters.UtcTimestamp
			],

			JournalingSlot.RollingBackPlan or JournalingSlot.RolledBackPlan =>
			[
				Parameters.Project, Parameters.Release, Parameters.Plan, Parameters.UtcTimestamp
			],

			JournalingSlot.RollingBackStep or JournalingSlot.RolledBackStep =>
			[
				Parameters.Project, Parameters.Release, Parameters.Plan, Parameters.Step, Parameters.UtcTimestamp
			],

			JournalingSlot.DeployingRelease or JournalingSlot.ReleaseDeployed =>
			[
				Parameters.Project, Parameters.Release, Parameters.Description, Parameters.UtcTimestamp
			],

			JournalingSlot.DeployingPlan or JournalingSlot.PlanDeployed =>
			[
				Parameters.Project,
				Parameters.Release,
				Parameters.Plan,
				Parameters.Description,
				Parameters.UtcTimestamp
			],

			JournalingSlot.DeployingStep or JournalingSlot.StepDeployed =>
			[
				Parameters.Project,
				Parameters.Release,
				Parameters.Plan,
				Parameters.Step,
				Parameters.Description,
				Parameters.UtcTimestamp
			],

			_ => throw new ArgumentOutOfRangeException( nameof(slot), slot, "Unknown journaling slot." )
		};

	/// <summary>
	/// Resolves a slot named on a command line, case-insensitively. Rejects the numeric forms
	/// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would otherwise accept.
	/// </summary>
	public static bool TryParse( string? name, out JournalingSlot slot )
	{
		slot = default;

		if( string.IsNullOrWhiteSpace( name ) || !char.IsLetter( name.Trim()[0] ) )
			return false;

		return Enum.TryParse( name.Trim(), ignoreCase: true, out slot ) && Enum.IsDefined( slot );
	}
}