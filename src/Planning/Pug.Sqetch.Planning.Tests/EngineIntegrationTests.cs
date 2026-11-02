using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;
using Pug.Sqetch.Tests.Planning.Stores.FileSystem;

namespace Pug.Sqetch.Planning.Tests;

/// <summary>
/// Drives the real <see cref="IProject"/> engine over the file-system stores, wired the
/// way the CLI wires them.
/// </summary>
public class EngineIntegrationTests
{
	[Fact]
	public void FullReleaseLifecycle()
	{
		using TempProject temp =
			TempProject.Create( ShardingCases.Configuration( VersionPrefixShardingStrategy.StrategyName ) );

		using FileSystemProjectStores stores = temp.Open();

		using IProject project = ProjectFactory.Create(
			stores.InfoStore,
			stores.ScriptsStore,
			new ReleaseChainDependencyDeterminator( stores.InfoStore ),
			TestData.User
		);

		project.Add( new PlanDefinition( "customer-email", "Add e-mail column", [ ] ) );
		project.Add( new PlanDefinition( "customer-index", "Index the e-mail column", [ "customer-email" ] ) );

		project.Add( new StepDefinition( "customer-email", "add-column", "adds the column", [ ] ), "customer-email" );

		Assert.Single( project.GetSteps( "customer-email" ) );

		project.CreateRelease(
			new ReleaseDefinition( "2026.07", "July release", "" ),
			[ "customer-email", "customer-index" ]
		);

		Assert.Equal( 2, project.GetPlans( new PlanSearchCriteria( Release: "2026.07" ) ).Count() );

		// removing a plan that another release member depends on must be refused
		Assert.Throws<AbandonedPlanDependantsException>( () =>
			project.RemovePlanFromRelease( "2026.07", "customer-email" )
		);

		project.FinalizeRelease( "2026.07" );

		Assert.NotNull( stores.InfoStore.GetRelease( "2026.07" )!.Finalized );
		Assert.Throws<PlanFinalizedException>( () => project.DeletePlan( "customer-email" ) );

		project.Add( new PlanDefinition( "cleanup", "Remove legacy column", [ ] ) );

		Assert.Throws<ReleaseFinalizedException>( () => project.AddPlanToRelease(
				"2026.07",
				"cleanup",
				includeDependencies: false
			)
		);

		// a follow-up release can depend on the finalized one
		project.CreateRelease( new ReleaseDefinition( "2026.08", "August release", "2026.07" ), [ "cleanup" ] );

		Assert.Equal(
			[ "2026.08" ],
			project.GetReleaseDependants( "2026.07" ).Select( x => x.Definition.Name ).ToArray()
		);

		// registration context carries the wired user identity
		Assert.Equal( TestData.User, stores.InfoStore.GetPlan( "cleanup" )!.Registration.Subject );
	}

	[Fact]
	public void ReleaseDependencyIsRequiredExceptForTheFirstRelease()
	{
		using TempProject temp =
			TempProject.Create( ShardingCases.Configuration( VersionPrefixShardingStrategy.StrategyName ) );

		using FileSystemProjectStores stores = temp.Open();

		using IProject project = ProjectFactory.Create(
			stores.InfoStore,
			stores.ScriptsStore,
			new ReleaseChainDependencyDeterminator( stores.InfoStore ),
			TestData.User
		);

		// only the very first release may omit its dependency
		project.CreateRelease( new ReleaseDefinition( "2026.01", "", "" ) );

		Assert.Throws<ReleaseDependencyRequiredException>( () =>
			project.CreateRelease( new ReleaseDefinition( "2026.02", "", "" ) )
		);

		Assert.Throws<ReleaseDependencyRequiredException>( () => project.CreateRelease(
				new ReleaseDefinition( "2026.02", "", "" ),
				Array.Empty<string>()
			)
		);

		// a declared dependency must name an existing release
		Assert.Throws<UnknownReleaseException>( () =>
			project.CreateRelease( new ReleaseDefinition( "2026.02", "", "2025.12" ) )
		);

		project.CreateRelease( new ReleaseDefinition( "2026.02", "", "2026.01" ) );

		Assert.NotNull( stores.InfoStore.GetRelease( "2026.02" ) );
	}

	[Fact]
	public void AReleaseMayHaveOnlyOneDependant()
	{
		using TempProject temp =
			TempProject.Create( ShardingCases.Configuration( VersionPrefixShardingStrategy.StrategyName ) );

		using FileSystemProjectStores stores = temp.Open();

		using IProject project = ProjectFactory.Create(
			stores.InfoStore,
			stores.ScriptsStore,
			new ReleaseChainDependencyDeterminator( stores.InfoStore ),
			TestData.User
		);

		project.CreateRelease( new ReleaseDefinition( "2026.01", "", "" ) );
		project.CreateRelease( new ReleaseDefinition( "2026.02", "", "2026.01" ) );

		// '2026.01' already has dependant '2026.02': a second lineage may not branch off it
		Assert.Throws<ReleaseDependantExistsException>( () =>
			project.CreateRelease( new ReleaseDefinition( "2026.03", "", "2026.01" ) )
		);

		Assert.Throws<ReleaseDependantExistsException>( () => project.CreateRelease(
				new ReleaseDefinition( "2026.03", "", "2026.01" ),
				Array.Empty<string>()
			)
		);

		// extending the chain at its tip is fine
		project.CreateRelease( new ReleaseDefinition( "2026.03", "", "2026.02" ) );

		Assert.NotNull( stores.InfoStore.GetRelease( "2026.03" ) );
	}
}