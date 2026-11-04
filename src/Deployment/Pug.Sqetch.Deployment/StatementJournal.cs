using System.Data;

using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Deployment;

/// <summary>
/// Runs a project's <see cref="JournalingStatements"/> against a database transaction. Replaces
/// the change-journal-writer family outright — nothing here is pluggable, since the SQL itself is
/// now the maintainer's business and the driver contributes only parameter binding.
///
/// Constructed once, from the statements a bundle carries and the project name; every member then
/// takes the transaction it runs in, so the engine's call sites read as they always did with the
/// collaborator swapped underneath them.
/// </summary>
public sealed class StatementJournal
{
	private readonly JournalingStatements _statements;

	private readonly string _project;

	private readonly Func<DateTime> _utcNow;

	/// <param name="utcNow">
	/// The clock behind the 'utcTimestamp' parameter, defaulting to <see cref="DateTime.UtcNow"/>.
	/// Injectable so the parameter contracts can be asserted against a fixed instant rather than
	/// whatever the test machine's clock said.
	/// </param>
	public StatementJournal( JournalingStatements statements, string project, Func<DateTime>? utcNow = null )
	{
		ArgumentNullException.ThrowIfNull( statements );
		ArgumentNullException.ThrowIfNull( project );

		_statements = statements;
		_project = project;
		_utcNow = utcNow ?? ( () => DateTime.UtcNow );
	}

	/// <summary>
	/// Migrates the journal to the shape <see cref="JournalingSlot.PrepareJournal"/>'s statements
	/// expect. Runs on every deployment, so each statement must be idempotent — safe to run again
	/// against a database it has already prepared — which the wrapped failure message repeats,
	/// since this is the slot's real obligation and not obvious from "prepare failed".
	/// </summary>
	public void PrepareJournal( IDatabaseTransaction transaction )
		=> Execute(
			JournalingSlot.PrepareJournal,
			Parameters( JournalingSlot.PrepareJournal ),
			transaction,
			"PrepareJournal statements must be safe to run again against a database they have already " +
			"prepared, since prepare runs on every deployment."
		);

	/// <summary>
	/// The release the journal is at, or null for a fresh database. Reads columns by ordinal, not
	/// name — Oracle folds unquoted identifiers to upper case, and <c>GetOrdinal</c>'s case
	/// sensitivity and thrown exception type are both provider-defined. Column 0 is the release name
	/// and must not be NULL: the meaningful unreleased-plans pseudo-release is the empty string, and
	/// coercing NULL to it would silently place the database at that pseudo-release. Column 1 is
	/// whether the release completed. More than one row is a corrupt journal and throws.
	/// </summary>
	public JournaledRelease? GetLatestRelease( IDatabaseTransaction transaction )
	{
		const string slot = nameof(JournalingSlot.GetLatestRelease);

		using IDataReader reader = ExecuteQuery( JournalingSlot.GetLatestRelease, transaction );

		if( reader.FieldCount < 2 )
			throw new JournalingStatementException(
				slot,
				1,
				$"must select at least two columns (release name, completed) but selected {reader.FieldCount}."
			);

		if( !reader.Read() )
			return null;

		if( reader.IsDBNull( 0 ) )
			throw new JournalingStatementException(
				slot,
				1,
				"column 0 (release name) is NULL; the unreleased-plans pseudo-release is the empty " +
				"string, never NULL."
			);

		string name = reader.GetValue( 0 ).ToString() ?? "";
		bool completed = Truth( slot, "column 1 (completed)", reader.GetValue( 1 ) );

		if( reader.Read() )
			throw new JournalingStatementException(
				slot,
				1,
				"returned more than one row; a journal reporting two latest releases is corrupt."
			);

		return new JournaledRelease( name, completed );
	}

	/// <summary>
	/// The plans of <paramref name="release"/> the journal holds, in no particular order — resume
	/// treats them as a set. Column 0 is the plan name and must not be NULL.
	/// </summary>
	public IReadOnlyList<string> GetDeployedPlans( string release, IDatabaseTransaction transaction )
	{
		const string slot = nameof(JournalingSlot.GetDeployedPlans);

		using IDataReader reader = ExecuteQuery( JournalingSlot.GetDeployedPlans, transaction, release: release );

		List<string> plans = [ ];

		while( reader.Read() )
		{
			if( reader.IsDBNull( 0 ) )
				throw new JournalingStatementException( slot, 1, "column 0 (plan name) is NULL." );

			plans.Add( reader.GetValue( 0 ).ToString() ?? "" );
		}

		return plans;
	}

	/// <summary>
	/// Whether the journal exists yet, from the optional <see cref="JournalingSlot.JournalExists"/>
	/// query: true when its first row's column 0 is true, false when it is false or there is no row.
	/// Column 0 coerces like <see cref="GetLatestRelease"/>'s completed column. Null when the slot is
	/// not set, so a caller decides whether it can go on without it.
	/// </summary>
	public bool? JournalExists( IDatabaseTransaction transaction )
	{
		if( !_statements.IsSet( JournalingSlot.JournalExists ) )
			return null;

		const string slot = nameof(JournalingSlot.JournalExists);

		using IDataReader reader = ExecuteQuery( JournalingSlot.JournalExists, transaction );

		if( reader.FieldCount < 1 )
			throw new JournalingStatementException( slot, 1, "must select at least one column (exists)." );

		return reader.Read() && Truth( slot, "column 0 (exists)", reader.GetValue( 0 ) );
	}

	/// <summary>
	/// Takes the database's deployment lock through the optional <see cref="JournalingSlot.AcquireLock"/>
	/// query: true when it was taken, false when its first row's column 0 is false or there is no row —
	/// another deployment holds it. Column 0 coerces like <see cref="GetLatestRelease"/>'s completed
	/// column. Null when the slot is not set: the deployment then runs unguarded, as it always did.
	/// </summary>
	public bool? AcquireLock( IDatabaseTransaction transaction )
	{
		if( !_statements.IsSet( JournalingSlot.AcquireLock ) )
			return null;

		const string slot = nameof(JournalingSlot.AcquireLock);

		using IDataReader reader = ExecuteQuery( JournalingSlot.AcquireLock, transaction );

		if( reader.FieldCount < 1 )
			throw new JournalingStatementException( slot, 1, "must select at least one column (acquired)." );

		return reader.Read() && Truth( slot, "column 0 (acquired)", reader.GetValue( 0 ) );
	}

	/// <summary>
	/// Gives back the lock <see cref="AcquireLock"/> took, through the optional
	/// <see cref="JournalingSlot.ReleaseLock"/> statements; does nothing when they are not set, leaving
	/// a session lock to end with the connection.
	/// </summary>
	public void ReleaseLock( IDatabaseTransaction transaction )
	{
		if( _statements.IsSet( JournalingSlot.ReleaseLock ) )
			Execute( JournalingSlot.ReleaseLock, Parameters( JournalingSlot.ReleaseLock ), transaction );
	}

	public void DeployingRelease( string release, string description, IDatabaseTransaction transaction )
		=> Execute(
			JournalingSlot.DeployingRelease,
			Parameters( JournalingSlot.DeployingRelease, release: release, description: description ),
			transaction
		);

	public void DeployingPlan( string release, string plan, string description, IDatabaseTransaction transaction )
		=> Execute(
			JournalingSlot.DeployingPlan,
			Parameters( JournalingSlot.DeployingPlan, release: release, plan: plan, description: description ),
			transaction
		);

	public void DeployingStep(
		string release,
		string plan,
		string step,
		string description,
		IDatabaseTransaction transaction
	)
		=> Execute(
			JournalingSlot.DeployingStep,
			Parameters(
				JournalingSlot.DeployingStep,
				release: release,
				plan: plan,
				step: step,
				description: description
			),
			transaction
		);

	public void StepDeployed(
		string release,
		string plan,
		string step,
		string description,
		IDatabaseTransaction transaction
	)
		=> Execute(
			JournalingSlot.StepDeployed,
			Parameters(
				JournalingSlot.StepDeployed,
				release: release,
				plan: plan,
				step: step,
				description: description
			),
			transaction
		);

	public void PlanDeployed( string release, string plan, string description, IDatabaseTransaction transaction )
		=> Execute(
			JournalingSlot.PlanDeployed,
			Parameters( JournalingSlot.PlanDeployed, release: release, plan: plan, description: description ),
			transaction
		);

	public void ReleaseDeployed( string release, string description, IDatabaseTransaction transaction )
		=> Execute(
			JournalingSlot.ReleaseDeployed,
			Parameters( JournalingSlot.ReleaseDeployed, release: release, description: description ),
			transaction
		);

	public void RollingBackRelease( string release, IDatabaseTransaction transaction )
		=> Execute(
			JournalingSlot.RollingBackRelease,
			Parameters( JournalingSlot.RollingBackRelease, release: release ),
			transaction
		);

	public void RollingBackPlan( string release, string plan, IDatabaseTransaction transaction )
		=> Execute(
			JournalingSlot.RollingBackPlan,
			Parameters( JournalingSlot.RollingBackPlan, release: release, plan: plan ),
			transaction
		);

	public void RollingBackStep( string release, string plan, string step, IDatabaseTransaction transaction )
		=> Execute(
			JournalingSlot.RollingBackStep,
			Parameters( JournalingSlot.RollingBackStep, release: release, plan: plan, step: step ),
			transaction
		);

	public void RolledBackStep( string release, string plan, string step, IDatabaseTransaction transaction )
		=> Execute(
			JournalingSlot.RolledBackStep,
			Parameters( JournalingSlot.RolledBackStep, release: release, plan: plan, step: step ),
			transaction
		);

	public void RolledBackPlan( string release, string plan, IDatabaseTransaction transaction )
		=> Execute(
			JournalingSlot.RolledBackPlan,
			Parameters( JournalingSlot.RolledBackPlan, release: release, plan: plan ),
			transaction
		);

	public void RolledBackRelease( string release, IDatabaseTransaction transaction )
		=> Execute(
			JournalingSlot.RolledBackRelease,
			Parameters( JournalingSlot.RolledBackRelease, release: release ),
			transaction
		);

	/// <summary>
	/// Runs every statement of <paramref name="slot"/> in order, wrapping a driver failure in
	/// <see cref="JournalingStatementException"/> naming the slot and the statement's 1-based
	/// position within it — with several statements per slot, the slot name alone does not say which
	/// line to look at.
	/// </summary>
	private void Execute(
		JournalingSlot slot,
		IReadOnlyList<JournalingParameter> parameters,
		IDatabaseTransaction transaction,
		string? failureHint = null
	)
	{
		IReadOnlyList<string> statements = _statements.Statements( slot );

		for( int index = 0; index < statements.Count; index++ )
			try
			{
				transaction.ExecuteJournalingStatement( statements[index], parameters );
			}
			catch( Exception exception )
			{
				string message = failureHint is null ? exception.Message : $"{exception.Message} {failureHint}";

				throw new JournalingStatementException( slot.ToString(), index + 1, message, exception );
			}
	}

	/// <summary>
	/// Runs a query slot's single statement (query slots hold exactly one — enforced by
	/// <see cref="JournalingStatements"/> — so a result set has somewhere unambiguous to come from)
	/// and returns its reader; the caller disposes it, as it is here the caller.
	/// </summary>
	private IDataReader ExecuteQuery( JournalingSlot slot, IDatabaseTransaction transaction, string? release = null )
	{
		string statement = _statements.Statements( slot )[0];
		IReadOnlyList<JournalingParameter> parameters = Parameters( slot, release: release );

		try
		{
			return transaction.ExecuteJournalingQuery( statement, parameters );
		}
		catch( Exception exception )
		{
			throw new JournalingStatementException( slot.ToString(), 1, exception.Message, exception );
		}
	}

	/// <summary>
	/// The parameters a slot's statements are called with, in the order
	/// <see cref="JournalingSlots.ParameterNames"/> lists for that slot — <c>@project</c> is always
	/// bound, the rest only when the slot's contract names them, so a caller never supplies a
	/// parameter the slot does not list.
	///
	/// The clock is read once per journaled event, not once per statement, so a slot holding several
	/// statements records one instant rather than a spread of them.
	/// </summary>
	private JournalingParameter[] Parameters(
		JournalingSlot slot,
		string? release = null,
		string? plan = null,
		string? step = null,
		string? description = null
	)
	{
		IReadOnlyList<string> names = JournalingSlots.ParameterNames( slot );
		JournalingParameter[] parameters = new JournalingParameter[names.Count];
		DateTime utcNow = names.Contains( JournalingSlots.Parameters.UtcTimestamp ) ? UtcNow() : default;

		for( int index = 0; index < names.Count; index++ )
			parameters[index] = new JournalingParameter(
				names[index],
				Value( names[index], release, plan, step, description, utcNow )
			);

		return parameters;
	}

	/// <summary>
	/// The clock's reading, forced to <see cref="DateTimeKind.Utc"/> — a driver binding an
	/// unspecified-kind <see cref="DateTime"/> may convert it as local time, and the parameter's whole
	/// contract is that it is UTC.
	/// </summary>
	private DateTime UtcNow()
	{
		DateTime now = _utcNow();

		return now.Kind switch
		{
			DateTimeKind.Utc => now,
			DateTimeKind.Local => now.ToUniversalTime(),
			_ => DateTime.SpecifyKind( now, DateTimeKind.Utc )
		};
	}

	private object Value(
		string name,
		string? release,
		string? plan,
		string? step,
		string? description,
		DateTime utcNow
	)
		=> name switch
		{
			JournalingSlots.Parameters.Project => _project,
			JournalingSlots.Parameters.Release => release!,
			JournalingSlots.Parameters.Plan => plan!,
			JournalingSlots.Parameters.Step => step!,
			JournalingSlots.Parameters.Description => description!,
			JournalingSlots.Parameters.UtcTimestamp => utcNow,
			_ => throw new ArgumentOutOfRangeException( nameof(name), name, "Unknown journaling parameter." )
		};

	/// <summary>
	/// Coerces a yes/no column (completed, exists) explicitly rather than with <see cref="Convert.ToBoolean(object)"/>,
	/// which throws on 'Y' — exactly what an Oracle or DB2 maintainer stores in a <c>CHAR(1)</c>. NULL
	/// throws: there is no valid completed state for a NULL. A <see cref="bool"/> passes through; any
	/// numeric type is compared to zero; a string or char matches "1", "y", "yes", "t", "true" as true
	/// and "0", "n", "no", "f", "false" as false, case-insensitively; anything else throws.
	/// </summary>
	private static bool Truth( string slot, string column, object value )
		=> value switch
		{
			DBNull => throw new JournalingStatementException(
				slot,
				1,
				$"{column} is NULL; there is no valid coercion for NULL."
			),

			bool boolean => boolean,

			byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
				Convert.ToDouble( value ) != 0,

			char c => TruthText( slot, column, c.ToString() ),

			string text => TruthText( slot, column, text ),

			_ => throw new JournalingStatementException(
				slot,
				1,
				$"{column} has an unsupported type '{value.GetType()}'."
			)
		};

	private static bool TruthText( string slot, string column, string text )
		=> text.Trim().ToLowerInvariant() switch
		{
			"1" or "y" or "yes" or "t" or "true" => true,
			"0" or "n" or "no" or "f" or "false" => false,
			_ => throw new JournalingStatementException(
				slot,
				1,
				$"{column} has an unrecognized value '{text}'."
			)
		};
}