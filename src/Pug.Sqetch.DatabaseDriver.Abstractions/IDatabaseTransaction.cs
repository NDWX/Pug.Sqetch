using System.Data;

namespace Pug.Sqetch.DatabaseDriver;

/// <summary>
/// Provides an abstraction for managing database transactions.
/// This interface defines methods for executing upgrade step scripts and
/// change journaling related statements and queries within the context of a transaction,
/// as well as committing or rolling back the transaction.
/// </summary>
public interface IDatabaseTransaction
{
    /// <summary>
    /// Executes the provided upgrade step script within the context of the transaction. Step scripts
    /// take no parameters: they are the maintainer's reviewed artifact and run verbatim. Only
    /// journaling statements are parameterized, since they are executed with values the engine
    /// supplies rather than authored inline.
    /// </summary>
    /// <param name="script">The upgrade step script to be executed.</param>
    void ExecuteStepScript(string script);

    /// <summary>
    /// Executes the specified journaling statement within the context of the transaction.
    /// </summary>
    /// <param name="statement">The journaling statement to be executed.</param>
    /// <param name="parameters">
    /// The parameters the statement's SQL refers to, by their bare name. An ordered list rather than
    /// a dictionary, since a dictionary's comparer is not part of this interface's contract (so
    /// '@Release' vs '@release' would become per-driver luck) and a provider that binds parameters
    /// positionally needs an order to bind them in.
    /// </param>
    void ExecuteJournalingStatement(string statement, IReadOnlyList<JournalingParameter> parameters);

    /// <summary>
    /// Executes the specified journaling query within the context of the transaction and returns the result set.
    /// </summary>
    /// <param name="query">The journaling query to be executed.</param>
    /// <param name="parameters">The parameters the query's SQL refers to, by their bare name. See <see cref="ExecuteJournalingStatement"/> for why this is a list rather than a dictionary.</param>
    /// <returns>An <see cref="IDataReader"/> containing the result set of the executed query. The caller disposes it.</returns>
    IDataReader ExecuteJournalingQuery(string query, IReadOnlyList<JournalingParameter> parameters);

    /// <summary>
    /// Rolls back transaction
    /// </summary>
    void Rollback();

    /// <summary>
    /// Commits transaction
    /// </summary>
    void Commit();
}