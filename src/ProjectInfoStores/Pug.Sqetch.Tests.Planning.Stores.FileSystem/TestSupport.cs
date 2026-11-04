using System.Diagnostics;

using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

namespace Pug.Sqetch.Tests.Planning.Stores.FileSystem;

public static class TestData
{
	public static readonly UserInfo User = new( "tester", "tester@example.com" );

	public static ActionContext Context() => new( User, DateTime.Now );
}

public static class ShardingCases
{
	public static TheoryData<string> Names => new(
		FlatShardingStrategy.StrategyName,
		VersionPrefixShardingStrategy.StrategyName
	);

	public static ShardingConfiguration Configuration( string name )
		=> name switch
		{
			FlatShardingStrategy.StrategyName => new ShardingConfiguration( FlatShardingStrategy.StrategyName ),
			VersionPrefixShardingStrategy.StrategyName => new ShardingConfiguration(
				VersionPrefixShardingStrategy.StrategyName,
				new Dictionary<string, string> { [VersionPrefixShardingStrategy.DelimiterOption] = "." }
			),
			_ => throw new ArgumentOutOfRangeException( nameof(name) )
		};
}

public sealed class TempProject : IDisposable
{
	private TempProject( string root )
	{
		Root = root;
	}

	public string Root { get; }

	public static TempProject Create(
		ShardingConfiguration? sharding = null,
		ReleaseShardingStrategyRegistry? registry = null
	)
	{
		string root = Path.Combine( Path.GetTempPath(), "sqetch-tests", Guid.NewGuid().ToString( "N" ) );

		FileSystemProjectStores.Initialize(
				new ProjectDefinition( "test-project", "Test project", "postgres" ),
				TestData.Context(),
				root,
				sharding,
				registry
			)
			.Dispose();

		return new TempProject( root );
	}

	public FileSystemProjectStores Open( ReleaseShardingStrategyRegistry? registry = null )
		=> FileSystemProjectStores.Open( Root, registry );

	public void Dispose()
	{
		try
		{
			Directory.Delete( Root, recursive: true );
		}
		catch( IOException )
		{
		}
	}
}

public static class Git
{
	public static string Run( string workingDirectory, params string[] arguments )
	{
		ProcessStartInfo start = new( "git" )
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};

		foreach( string argument in arguments )
			start.ArgumentList.Add( argument );

		using Process process = Process.Start( start )!;

		string output = process.StandardOutput.ReadToEnd();
		string error = process.StandardError.ReadToEnd();

		process.WaitForExit();

		if( process.ExitCode != 0 )
			throw new InvalidOperationException( $"git {string.Join( ' ', arguments )} failed: {error}" );

		return output;
	}

	public static void Commit( string workingDirectory, string message )
	{
		Run( workingDirectory, "add", "-A" );

		Run(
			workingDirectory,
			"-c",
			"user.name=tester",
			"-c",
			"user.email=tester@example.com",
			"commit",
			"-m",
			message,
			"--allow-empty"
		);
	}

	public static string StagedChanges( string workingDirectory )
	{
		Run( workingDirectory, "add", "-A" );

		return Run( workingDirectory, "diff", "--cached", "-M", "--name-status" );
	}
}
/// <summary>
/// A fact that needs Unix file permissions to be enforced: it skips on Windows, and as root,
/// which ignores them.
/// </summary>
public sealed class UnixPermissionsFactAttribute : FactAttribute
{
	public UnixPermissionsFactAttribute()
	{
		if( OperatingSystem.IsWindows() )
			Skip = "needs Unix file permissions";
		else if( Environment.IsPrivilegedProcess )
			Skip = "file permissions are not enforced for root";
	}
}
