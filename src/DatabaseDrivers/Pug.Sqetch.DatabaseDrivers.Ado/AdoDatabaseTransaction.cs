using System.Data;
using System.Data.Common;
using Pug.Sqetch.DatabaseDriver;

namespace Pug.Sqetch.DatabaseDrivers.Ado;

/// <summary>
/// One ADO.NET transaction: the scripts and journaling statements run inside it, and the single
/// commit or rollback that ends it.
/// </summary>
public sealed class AdoDatabaseTransaction : IDatabaseTransaction, IDisposable
{
	private readonly DbTransaction _transaction;

	private readonly TimeSpan _stepScriptTimeout;

	/// <summary>
	/// Readers handed to the journal, each with the command that produced it. The command must outlive
	/// its reader — disposing it closes the reader underneath the caller — so neither is disposed
	/// until the journal is done with the reader or the transaction ends, whichever comes first. The
	/// journal does dispose its readers, but a provider that allows only one open reader per
	/// connection would fail the next command if it ever did not, and that failure would name the
	/// provider's internals rather than the leak.
	/// </summary>
	private readonly List<(DbCommand Command, DbDataReader Reader)> _queries = [];

	internal AdoDatabaseTransaction( DbTransaction transaction, TimeSpan stepScriptTimeout )
	{
		_transaction = transaction;
		_stepScriptTimeout = stepScriptTimeout;
	}

	internal bool Finished { get; private set; }

	/// <summary>
	/// Runs the script as the maintainer wrote it, in one command — a step script is a reviewed
	/// artifact, holds as many statements as its author wants, and takes no parameters. The step
	/// script timeout applies here and nowhere else: it bounds the maintainer's own SQL, whereas a
	/// journaling statement waiting on a lock is better left to the provider's default.
	/// </summary>
	public void ExecuteStepScript( string script )
	{
		using DbCommand command = Command( script );

		command.CommandTimeout = CommandTimeout( _stepScriptTimeout );

		command.ExecuteNonQuery();
	}

	public void ExecuteJournalingStatement( string statement, IReadOnlyList<JournalingParameter> parameters )
	{
		using DbCommand command = Command( statement );

		Bind( command, parameters );

		command.ExecuteNonQuery();
	}

	public IDataReader ExecuteJournalingQuery( string query, IReadOnlyList<JournalingParameter> parameters )
	{
		DbCommand command = Command( query );

		try
		{
			Bind( command, parameters );

			DbDataReader reader = command.ExecuteReader();

			_queries.Add( ( command, reader ) );

			return reader;
		}
		catch
		{
			command.Dispose();

			throw;
		}
	}

	public void Commit()
	{
		CloseReaders();

		_transaction.Commit();

		Finished = true;
	}

	public void Rollback()
	{
		CloseReaders();

		_transaction.Rollback();

		Finished = true;
	}

	public void Dispose()
	{
		CloseReaders();

		// DbTransaction.Dispose rolls back an uncommitted transaction, which is what an abandoned
		// one deserves
		_transaction.Dispose();

		Finished = true;
	}

	private DbCommand Command( string text )
	{
		DbCommand command = _transaction.Connection?.CreateCommand()
							?? throw new InvalidOperationException( "the transaction's connection is gone." );

		command.Transaction = _transaction;
		command.CommandText = text;

		return command;
	}

	/// <summary>
	/// Binds every parameter the slot carries, by its bare name. A statement binds what it mentions
	/// and Sqetch never scans the SQL, so the unmentioned ones are bound too and ignored by the
	/// provider — asserted per provider, since a provider that refused them instead would need the
	/// driver to scan.
	/// </summary>
	private static void Bind( DbCommand command, IReadOnlyList<JournalingParameter> parameters )
	{
		foreach( JournalingParameter parameter in parameters )
		{
			DbParameter bound = command.CreateParameter();

			bound.ParameterName = parameter.Name;
			bound.Value = parameter.Value;

			command.Parameters.Add( bound );
		}
	}

	private void CloseReaders()
	{
		foreach( (DbCommand command, DbDataReader reader) in _queries )
		{
			if( !reader.IsClosed )
				reader.Dispose();

			command.Dispose();
		}

		_queries.Clear();
	}

	/// <summary>
	/// <see cref="IDbCommand.CommandTimeout"/> counts whole seconds and takes zero to mean no limit,
	/// which is how <see cref="Timeout.InfiniteTimeSpan"/> arrives here. A sub-second timeout rounds
	/// up to one second rather than down to none at all.
	/// </summary>
	private static int CommandTimeout( TimeSpan timeout )
		=> timeout == Timeout.InfiniteTimeSpan || timeout < TimeSpan.Zero
			? 0
			: Math.Max( 1, (int)Math.Ceiling( timeout.TotalSeconds ) );
}
