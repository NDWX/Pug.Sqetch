using Pug.Sqetch.Cli;
using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Shared;

namespace Pug.Sqetch.Tests.Cli.Common;

/// <summary>
/// Where 'sqetch journaling init' finds templates: the nearest repository-level '.config/sqetch',
/// then the user level, then the driver's own, each template used whole.
/// </summary>
public sealed class JournalingTemplateSourcesTests : IDisposable
{
	private readonly string _temp = Path.Combine( Path.GetTempPath(), "sqetch-template-tests", Guid.NewGuid().ToString( "N" ) );

	private readonly DatabaseDriverRegistry _drivers = new();

	public JournalingTemplateSourcesTests()
	{
		Directory.CreateDirectory( _temp );

		_drivers.Register( "withtemplate", () => new TemplatedFactory( "withtemplate" ) );
		_drivers.Register( "plain", () => new PlainFactory( "plain" ) );
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete( _temp, recursive: true );
		}
		catch( IOException )
		{
		}
	}

	private string Folder( params string[] parts )
	{
		string path = Path.Combine( [ _temp, ..parts ] );

		Directory.CreateDirectory( path );

		return path;
	}

	// a git work tree with the project two folders below its root
	private (string Repository, string Project) GitProject()
	{
		string repository = Folder( "repo" );

		Directory.CreateDirectory( Path.Combine( repository, ".git" ) );

		return (repository, Folder( "repo", "databases", "orders" ));
	}

	private static void WriteTemplate( string root, string driver, string name, string marker, params JournalingSlot[] omit )
	{
		string folder = Path.Combine( root, "database-drivers", driver, "journaling-templates", name );

		Directory.CreateDirectory( folder );

		foreach( JournalingSlot slot in JournalingSlots.All.Except( omit ) )
			File.WriteAllText( Path.Combine( folder, JournalingSlots.FileName( slot ) ), $"-- {marker} {slot}" );
	}

	private JournalingTemplateSources Sources( string? userRoot = null )
		=> new( _drivers, userRoot ?? Path.Combine( _temp, "no-user-root" ) );

	[Fact]
	public void TheNearestRepositoryRootWins()
	{
		(string repository, string project) = GitProject();

		Folder( "repo", ".config", "sqetch" );
		string nearer = Folder( "repo", "databases", ".config", "sqetch" );

		Assert.Equal( nearer, JournalingTemplateSources.FindRepositoryRoot( project ) );
	}

	[Fact]
	public void TheSearchStopsAtTheGitWorkTreeRoot()
	{
		Folder( ".config", "sqetch" );
		(_, string project) = GitProject();

		Assert.Null( JournalingTemplateSources.FindRepositoryRoot( project ) );
	}

	[Fact]
	public void TheProjectFolderItselfCounts()
	{
		string project = Folder( "standalone" );
		string own = Folder( "standalone", ".config", "sqetch" );

		Assert.Equal( own, JournalingTemplateSources.FindRepositoryRoot( project ) );
	}

	[Fact]
	public void RepositoryBeatsUserWhichBeatsBuiltIn()
	{
		(string repository, string project) = GitProject();
		string user = Folder( "user" );

		WriteTemplate( user, "withtemplate", "default", "user" );
		WriteTemplate( user, "withtemplate", "audit", "user" );

		JournalingTemplateSources sources = Sources( user );

		JournalingTemplate fromUser = sources.Resolve( project, "withtemplate" );

		Assert.Equal( JournalingTemplateLevel.User, fromUser.Level );
		Assert.Equal( "-- user PrepareJournal", fromUser.Statements[JournalingSlot.PrepareJournal] );

		WriteTemplate( Path.Combine( repository, ".config", "sqetch" ), "withtemplate", "default", "repo" );

		JournalingTemplate fromRepository = sources.Resolve( project, "WithTemplate" );

		Assert.Equal( JournalingTemplateLevel.Repository, fromRepository.Level );
		Assert.Equal( "withtemplate", fromRepository.Driver );
		Assert.Equal( "-- repo GetLatestRelease", fromRepository.Statements[JournalingSlot.GetLatestRelease] );

		// another name still comes from the user level
		Assert.Equal( JournalingTemplateLevel.User, sources.Resolve( project, "withtemplate", "audit" ).Level );

		Assert.Equal(
			[
				("default", JournalingTemplateLevel.Repository, false),
				("audit", JournalingTemplateLevel.User, false),
				("default", JournalingTemplateLevel.User, true),
				("default", JournalingTemplateLevel.BuiltIn, true)
			],
			sources.List( project, "withtemplate" ).Select( x => (x.Name, x.Level, x.Shadowed) ) );
	}

	[Fact]
	public void TheBuiltInTemplateIsUsedWhenNothingOverridesIt()
	{
		(_, string project) = GitProject();

		JournalingTemplate template = Sources().Resolve( project, "withtemplate" );

		Assert.Equal( JournalingTemplateLevel.BuiltIn, template.Level );
		Assert.Null( template.Location );
		Assert.Equal( JournalingSlots.All.Count, template.Statements.Count );
		Assert.Equal( "-- built in RolledBackRelease", template.Statements[JournalingSlot.RolledBackRelease] );
	}

	[Fact]
	public void AnIncompleteTemplateIsRefusedNamingWhatIsMissing()
	{
		string user = Folder( "user" );

		WriteTemplate( user, "withtemplate", "default", "user", JournalingSlot.GetDeployedPlans, JournalingSlot.DeployingStep );

		JournalingTemplateException error = Assert.Throws<JournalingTemplateException>(
			() => Sources( user ).Resolve( null, "withtemplate" ) );

		Assert.Contains( "GetDeployedPlans.sql", error.Message );
		Assert.Contains( "OnDeployingStep.sql", error.Message );
	}

	[Fact]
	public void AFileNamingNoSlotIsRefused()
	{
		string user = Folder( "user" );

		WriteTemplate( user, "withtemplate", "default", "user" );
		File.WriteAllText(
			Path.Combine( user, "database-drivers", "withtemplate", "journaling-templates", "default", "DeployingStep.sql" ),
			"-- the event name, not the project's file name" );

		JournalingTemplateException error = Assert.Throws<JournalingTemplateException>(
			() => Sources( user ).Resolve( null, "withtemplate" ) );

		Assert.Contains( "not a slot: DeployingStep.sql", error.Message );
	}

	[Fact]
	public void AnUnknownTemplateListsTheAvailableOnes()
	{
		string user = Folder( "user" );

		WriteTemplate( user, "plain", "team", "user" );

		JournalingTemplateException error = Assert.Throws<JournalingTemplateException>(
			() => Sources( user ).Resolve( null, "plain", "audit" ) );

		Assert.Contains( "available: team", error.Message );

		Assert.Contains(
			"it has none",
			Assert.Throws<JournalingTemplateException>( () => Sources().Resolve( null, "plain" ) ).Message );
	}

	[Fact]
	public void AnUnknownDriverIsRefusedAndItsFolderReported()
	{
		string user = Folder( "user" );

		WriteTemplate( user, "oracle", "default", "user" );

		Assert.Throws<UnknownDatabaseDriverException>( () => Sources( user ).Resolve( null, "oracle" ) );

		Assert.Equal(
			[ Path.Combine( user, "database-drivers", "oracle" ) ],
			Sources( user ).UnknownDriverFolders( null ) );
	}

	private class PlainFactory( string name ) : IDatabaseDriverFactory
	{
		public string Name => name;

		public DatabaseDriverParametersDefinition GetParametersDefinition() => new( [ ], [ ] );

		public IDatabaseDriver Create( IDictionary<string, string> parameters, TimeSpan stepScriptTimeout )
			=> throw new NotSupportedException();
	}

	private sealed class TemplatedFactory( string name ) : PlainFactory( name ), IJournalingTemplateProvider
	{
		public IReadOnlyDictionary<string, string> GetJournalingTemplate()
			=> JournalingSlots.All.ToDictionary( slot => slot.ToString(), slot => $"-- built in {slot}" );
	}
}
