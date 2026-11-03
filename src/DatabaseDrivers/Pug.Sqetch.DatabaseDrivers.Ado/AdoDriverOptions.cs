using System.Data.Common;

using Pug.Sqetch.DatabaseDriver;

namespace Pug.Sqetch.DatabaseDrivers.Ado;

/// <summary>
/// What a provider driver may change about the shared ADO.NET behaviour. Each hook exists because a
/// provider's test proved the need; the defaults are what SQLite and PostgreSQL use.
/// </summary>
public sealed record AdoDriverOptions
{
	public static AdoDriverOptions Default { get; } = new();

	/// <summary>
	/// Splits a step script into batches that run in order, as separate commands in the step's
	/// transaction — for a provider whose tooling separates batches client-side (SQL Server's 'GO').
	/// Null runs the script as one command, as written.
	/// </summary>
	public Func<string, IReadOnlyList<string>>? SplitStepScript { get; init; }

	/// <summary>
	/// Adjusts a journaling parameter after its name and value are bound — for a provider whose
	/// default type for a value loses part of it.
	/// </summary>
	public Action<DbParameter, JournalingParameter>? ConfigureParameter { get; init; }
}
