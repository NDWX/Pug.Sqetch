namespace Pug.Sqetch.DatabaseDrivers.Ado;

/// <summary>
/// The connection parameters every server driver takes — host, port, database, username, password
/// or the environment variable holding it, or a whole connection string instead — read the same way
/// for each, so the rules and their messages cannot drift between drivers.
/// </summary>
public sealed class ServerConnectionParameters( IDictionary<string, string> parameters )
{
	public const string ConnectionStringName = "connection-string";

	/// <summary>The value given for <paramref name="name"/>, or null when absent or blank.</summary>
	public string? Value( string name )
		=> parameters.TryGetValue( name, out string? value ) && !string.IsNullOrWhiteSpace( value ) ? value : null;

	public string Required( string name )
		=> Value( name ) ?? throw new ArgumentException( $"'{name}' is required unless '{ConnectionStringName}' is given." );

	public int? Port()
		=> Value( "port" ) is { } port ?
			int.TryParse( port, out int parsed ) ? parsed : throw new ArgumentException( $"'port' must be a number, not '{port}'." ) :
			null;

	/// <summary>
	/// 'connection-string' replaces the individual parameters rather than extending them: merging the
	/// two would leave it unclear which host won, and the whole point of the escape hatch is that the
	/// maintainer's string is used as written. Null when no connection string was given.
	/// </summary>
	public string? ConnectionString( params string[] individual )
	{
		string? supplied = Value( ConnectionStringName );

		if( supplied is not null && individual.Any( name => Value( name ) is not null ) )
			throw new ArgumentException(
				$"'{ConnectionStringName}' replaces the individual connection parameters; specify one or the other." );

		return supplied;
	}

	/// <summary>
	/// The password, from 'password' or from the environment variable 'password-env' names. A command
	/// line is readable by every process on the host and lands in shell history, so the environment
	/// variable is the one to reach for in a pipeline; naming the variable rather than reading a fixed
	/// one keeps it the maintainer's choice.
	/// </summary>
	public string? Password()
	{
		string? password = Value( "password" ), variable = Value( "password-env" );

		if( password is not null && variable is not null )
			throw new ArgumentException( "specify either 'password' or 'password-env', not both." );

		if( variable is null )
			return password;

		string? value = Environment.GetEnvironmentVariable( variable );

		if( string.IsNullOrEmpty( value ) )
			throw new ArgumentException( $"environment variable '{variable}' named by 'password-env' is not set." );

		return value;
	}
}
