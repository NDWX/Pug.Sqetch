using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

namespace Pug.Sqetch.Tests.Planning.Stores.FileSystem;

/// <summary>
/// Verifies the design goal end-to-end: every business event shows up in git as the
/// expected add / edit / rename, so a project's history reads like a changelog.
/// </summary>
public class GitLegibilityTests
{
	[Fact]
	public void BusinessEventsProduceLegibleDiffs()
	{
		using TempProject project = TempProject.Create();
		using FileSystemProjectStores stores = project.Open();

		Git.Run( project.Root, "init" );
		Git.Commit( project.Root, "initialize project" );

		stores.InfoStore.AddPlan(
			new ObjectDefinition( "customer-email", "Add e-mail column" ),
			[ ],
			TestData.Context()
		);

		string diff = Git.StagedChanges( project.Root );
		Assert.Contains( "A\tplans/customer-email/plan.json", diff );
		Assert.Contains( "M\tplan-index", diff );
		Git.Commit( project.Root, "add plan customer-email" );

		stores.InfoStore.AddStep( "customer-email", new ObjectDefinition( "add-column", "" ), [ ], TestData.Context() );

		using( MemoryStream deploy = new( "ALTER TABLE customer ADD email text;"u8.ToArray() ) )
			stores.ScriptsStore.PutStepScripts( "customer-email", "add-column", new StepScripts( deploy, null, null ) );

		diff = Git.StagedChanges( project.Root );
		Assert.Contains( "A\tplans/customer-email/steps/add-column/step.json", diff );
		Assert.Contains( "A\tplans/customer-email/steps/add-column/deploy.sql", diff );
		Git.Commit( project.Root, "add step add-column" );

		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.07", "July release", "" ), TestData.Context() );

		diff = Git.StagedChanges( project.Root );
		Assert.Contains( "A\treleases/2026.07/release.json", diff );
		Git.Commit( project.Root, "create release 2026.07" );

		stores.InfoStore.AddReleasePlan( "2026.07", "customer-email", TestData.Context() );

		diff = Git.StagedChanges( project.Root );

		// unchanged files inside the plan folder are detected as pure renames…
		Assert.Matches(
			@"R100\tplans/customer-email/steps/add-column/step\.json\treleases/2026\.07/plans/customer-email/steps/add-column/step\.json",
			diff
		);

		Assert.Matches(
			@"R100\tplans/customer-email/steps/add-column/deploy\.sql\treleases/2026\.07/plans/customer-email/steps/add-column/deploy\.sql",
			diff
		);

		// …and plan.json moves as a rename-with-edit (its release fields changed)
		Assert.Matches(
			@"R\d+\tplans/customer-email/plan\.json\treleases/2026\.07/plans/customer-email/plan\.json",
			diff
		);

		Assert.Contains( "M\tplan-index", diff );
		Git.Commit( project.Root, "add customer-email to release 2026.07" );

		stores.InfoStore.SetReleaseContext( "2026.07", TestData.Context() );

		// under flat sharding the release does not move, so finalization is a one-file edit
		diff = Git.StagedChanges( project.Root );
		Assert.Equal( "M\treleases/2026.07/release.json", diff.Trim() );
		Git.Commit( project.Root, "finalize release 2026.07" );
	}

	[Fact]
	public void JournalingStatementsAppearAsPlainFileEdits()
	{
		using TempProject project = TempProject.Create();
		using FileSystemProjectStores stores = project.Open();

		Git.Run( project.Root, "init" );
		Git.Commit( project.Root, "initialize project" );

		stores.InfoStore.SetJournalingStatement(
			JournalingSlot.PrepareJournal,
			"create table journal( release text )"
		);

		string diff = Git.StagedChanges( project.Root );
		Assert.Contains( "A\tjournaling/PrepareJournal.sql", diff );
		Git.Commit( project.Root, "set PrepareJournal" );

		stores.InfoStore.SetJournalingStatement(
			JournalingSlot.PrepareJournal,
			"create table journal( release text not null )"
		);

		diff = Git.StagedChanges( project.Root );
		Assert.Equal( "M\tjournaling/PrepareJournal.sql", diff.Trim() );
	}

	[Fact]
	public void FinalizationMovesTheReleaseIntoItsShard()
	{
		using TempProject project =
			TempProject.Create( ShardingCases.Configuration( VersionPrefixShardingStrategy.StrategyName ) );

		using FileSystemProjectStores stores = project.Open();

		Git.Run( project.Root, "init" );

		stores.InfoStore.AddPlan( new ObjectDefinition( "customer-email", "" ), [ ], TestData.Context() );
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.07", "", "" ), TestData.Context() );
		stores.InfoStore.AddReleasePlan( "2026.07", "customer-email", TestData.Context() );
		Git.Commit( project.Root, "prepare release 2026.07" );

		stores.InfoStore.SetReleaseContext( "2026.07", TestData.Context() );

		string diff = Git.StagedChanges( project.Root );

		// the release tree renames into its shard; only release.json itself changed content
		Assert.Matches(
			@"R100\treleases/2026\.07/plans/customer-email/plan\.json\treleases/2026/2026\.07/plans/customer-email/plan\.json",
			diff
		);

		Assert.Matches(
			@"R\d+\treleases/2026\.07/release\.json\treleases/2026/2026\.07/release\.json",
			diff
		);
	}
}