namespace Pug.Sqetch.DatabaseDriver;

/// <summary>
/// Represents the core interface for database driver implementations. It is purely a transaction
/// factory: journaling is always transactional, so there is no non-transactional counterpart to
/// <see cref="BeginTransaction"/> — running the journal's queries inside one transaction keeps a
/// deployment's resume decision read-consistent instead of two independent auto-commit round trips.
///
/// Disposable because a driver owns whatever it opened to serve those transactions — a connection,
/// a pool lease — and the host that created it is the only thing that knows when the last
/// transaction is done. Disposing it must roll back a transaction still open, never commit one.
/// </summary>
public interface IDatabaseDriver : IDisposable
{
    /// <summary>
    /// Return an instance of IDatabaseTransaction that can be used to execute change and journaling scripts within a single transaction.
    /// </summary>
    /// <returns>Instance of IDatabaseTransaction</returns>
    public IDatabaseTransaction BeginTransaction();
}