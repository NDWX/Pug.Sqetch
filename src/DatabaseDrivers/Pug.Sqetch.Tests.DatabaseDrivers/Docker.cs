using System.Net.Sockets;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// Whether this host can run the PostgreSQL container these tests need. Probed by connecting to the
/// daemon's socket rather than by shelling out: the failure mode worth detecting here is a shell
/// whose process is not in the 'docker' group, which looks exactly like a permission error on the
/// socket and nothing like a missing Docker.
/// </summary>
public static class Docker
{
	private static readonly Lazy<bool> Probe = new ( Reachable );

	public static bool Available => Probe.Value;

	public static string Reason =>
		"requires a reachable Docker daemon; run the suite under 'sg docker -c \"dotnet test ...\"' if the "
		+ "account's group membership predates this shell";

	private static bool Reachable()
	{
		string host = Environment.GetEnvironmentVariable( "DOCKER_HOST" ) ?? "unix:///var/run/docker.sock";

		if( !host.StartsWith( "unix://", StringComparison.Ordinal ) )
			return true; // a TCP or named-pipe endpoint is Testcontainers' problem, not ours to second-guess

		try
		{
			using Socket socket = new ( AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified );

			socket.Connect( new UnixDomainSocketEndPoint( host["unix://".Length..] ) );

			return true;
		}
		catch( Exception )
		{
			return false;
		}
	}
}

/// <summary>A fact that skips, rather than fails, where Docker cannot be reached.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
	public DockerFactAttribute()
	{
		if( !Docker.Available )
			Skip = Docker.Reason;
	}
}

/// <summary>A theory that skips, rather than fails, where Docker cannot be reached.</summary>
public sealed class DockerTheoryAttribute : TheoryAttribute
{
	public DockerTheoryAttribute()
	{
		if( !Docker.Available )
			Skip = Docker.Reason;
	}
}
