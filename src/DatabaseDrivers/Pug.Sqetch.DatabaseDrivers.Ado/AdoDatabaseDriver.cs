using System.Data.Common;
using Pug.Sqetch.DatabaseDriver;

namespace Pug.Sqetch.DatabaseDrivers.Ado;

/// <summary>
/// The ADO.NET half of every driver: a connection, the transactions opened on it, and the command
/// plumbing underneath them. A provider-specific driver supplies a connection factory and inherits
/// the rest, so a fix to transaction or parameter handling lands in one place rather than once per
/// provider.
///
/// Parameters are bound by their bare name, which both providers so far resolve against every
/// placeholder form they accept, and all of them are bound whether the statement mentions them or
/// not, which both providers accept. A provider that needs the prefix spelled out, or that refuses a
/// parameter it cannot see, needs that handled here — with a test proving the need, since neither
/// behaviour can be guessed from a provider's documentation.
///
/// One connection, opened on first use and reused: a deployment's transactions are strictly
/// sequential — prepare, then the journal read, then the deployment itself — so there is never a
/// second one to serve, and session state a maintainer's statements set up (a search path, a PRAGMA)
/// survives from one transaction to the next.
/// </summary>
public class AdoDatabaseDriver : IDatabaseDriver
{
	private readonly Func<DbConnection> _connect;

	private readonly TimeSpan _stepScriptTimeout;

	private DbConnection? _connection;

	private AdoDatabaseTransaction? _transaction;

	private bool _disposed;

	public AdoDatabaseDriver( Func<DbConnection> connect, TimeSpan stepScriptTimeout )
	{
		ArgumentNullException.ThrowIfNull( connect );

		_connect = connect;
		_stepScriptTimeout = stepScriptTimeout;
	}

	public IDatabaseTransaction BeginTransaction()
	{
		ObjectDisposedException.ThrowIf( _disposed, this );

		// the engine never overlaps transactions, so this is a bug report rather than a limitation:
		// the provider would otherwise either nest silently or fail with a message about its own
		// internals instead of about the caller
		if( _transaction is { Finished: false } )
			throw new InvalidOperationException(
				"a transaction is already open on this connection; commit or roll it back first." );

		DbConnection connection = Connection();

		_transaction = new AdoDatabaseTransaction( connection.BeginTransaction(), _stepScriptTimeout );

		return _transaction;
	}

	private DbConnection Connection()
	{
		if( _connection is not null )
			return _connection;

		DbConnection connection = _connect();

		try
		{
			connection.Open();
		}
		catch( Exception exception )
		{
			connection.Dispose();

			// a driver is configured without connecting, so this is the first moment the server is
			// known to be out of reach — said in those terms rather than as a provider error, since
			// the caller's remedy is the host, the credentials or the network, not the deployment
			throw new DatabaseConnectionException( exception );
		}

		return _connection = connection;
	}

	public void Dispose()
	{
		Dispose( true );

		GC.SuppressFinalize( this );
	}

	protected virtual void Dispose( bool disposing )
	{
		if( _disposed || !disposing )
			return;

		_disposed = true;

		// an unfinished transaction is rolled back by disposing it: a deployment that threw
		// between begin and commit must not have its work committed by the connection closing
		_transaction?.Dispose();
		_connection?.Dispose();
	}
}
