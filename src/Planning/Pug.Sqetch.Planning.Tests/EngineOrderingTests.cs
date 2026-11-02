using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;
using Pug.Sqetch.Tests.Planning.Stores.FileSystem;

namespace Pug.Sqetch.Planning.Tests;

/// <summary>
/// Dependency-chronological ordering is the engine's responsibility: the store only filters
/// (and pre-groups released plans by release chronology). Data is seeded through the store
/// so registration timestamps can deliberately contradict the dependency order.
/// </summary>
public class EngineOrderingTests
{
	private static readonly UserInfo Alice = new( "alice", "alice@example.com" );

	private static ActionContext At( string timestamp ) => new( Alice, DateTime.Parse( timestamp ) );

	private static IProject Wire( FileSystemProjectStores stores )
		=> ProjectFactory.Create(
			stores.InfoStore,
			stores.ScriptsStore,
			new ReleaseChainDependencyDeterminator( stores.InfoStore ),
			TestData.User
		);

	[Fact]
	public void ReleasesFollowTheirDependencyChainDespiteTimestamps()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = Wire( stores );

		// the dependant is registered with the EARLIEST timestamp; only the chain can order it last
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.03", "", "2026.02" ), At( "2026-01-01" ) );
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.02", "", "2026.01" ), At( "2026-05-01" ) );
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.01", "", "" ), At( "2026-09-01" ) );
		stores.InfoStore.AddRelease( new ReleaseDefinition( "standalone", "", "" ), At( "2026-02-01" ) );

		Assert.Equal(
			[ "standalone", "2026.01", "2026.02", "2026.03" ],
			project.GetReleases( new ReleaseSearchCriteria() ).Select( x => x.Definition.Name ).ToArray()
		);
	}

	[Fact]
	public void PlansAreOrderedByReleaseChronologyThenDependencies()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = Wire( stores );

		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.01", "", "" ), At( "2026-01-01" ) );
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.02", "", "2026.01" ), At( "2026-02-01" ) );

		// second release's plans registered EARLIER than the first release's, and within the
		// first release the dependant plan is registered before its dependency
		stores.InfoStore.AddPlan( new ObjectDefinition( "table", "" ), [ ], At( "2026-01-20" ) );
		stores.InfoStore.AddPlan( new ObjectDefinition( "index", "" ), [ "table" ], At( "2026-01-10" ) );
		stores.InfoStore.AddPlan( new ObjectDefinition( "cleanup", "" ), [ ], At( "2026-01-05" ) );

		stores.InfoStore.AddReleasePlan( "2026.01", "table", At( "2026-01-21" ) );
		stores.InfoStore.AddReleasePlan( "2026.01", "index", At( "2026-01-21" ) );
		stores.InfoStore.AddReleasePlan( "2026.02", "cleanup", At( "2026-02-02" ) );

		Assert.Equal(
			[ "table", "index", "cleanup" ],
			project.GetPlans( new PlanSearchCriteria( Released: true ) ).Select( x => x.Definition.Name ).ToArray()
		);
	}

	[Fact]
	public void UnreleasedPlansAreOrderedByDependenciesThenTimestamp()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = Wire( stores );

		// 'blocked' is registered first but must wait for its dependency 'released-later',
		// which itself outwaits nothing; 'independent' has the latest registration of all
		stores.InfoStore.AddPlan( new ObjectDefinition( "blocked", "" ), [ "released-later" ], At( "2026-01-01" ) );
		stores.InfoStore.AddPlan( new ObjectDefinition( "released-later", "" ), [ ], At( "2026-01-03" ) );
		stores.InfoStore.AddPlan( new ObjectDefinition( "independent", "" ), [ ], At( "2026-01-05" ) );

		Assert.Equal(
			[ "released-later", "blocked", "independent" ],
			project.GetPlans( new PlanSearchCriteria() ).Select( x => x.Definition.Name ).ToArray()
		);
	}

	[Fact]
	public void StepsFollowTheirDependenciesDespiteTimestamps()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = Wire( stores );

		stores.InfoStore.AddPlan( new ObjectDefinition( "migration", "" ), [ ], At( "2026-01-01" ) );

		stores.InfoStore.AddStep(
			"migration",
			new ObjectDefinition( "verify", "" ),
			[ "create-table" ],
			At( "2026-01-02" )
		);

		stores.InfoStore.AddStep(
			"migration",
			new ObjectDefinition( "create-table", "" ),
			[ ],
			At( "2026-01-05" )
		);

		Assert.Equal(
			[ "create-table", "verify" ],
			project.GetSteps( "migration" ).Select( x => x.Definition.Name ).ToArray()
		);
	}
}