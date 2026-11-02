using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

namespace Pug.Sqetch.Tests.Planning.Stores.FileSystem;

/// <summary>
/// Project-level journaling SQL is stored one file per <see cref="JournalingSlot"/> under
/// 'journaling', the same technique step scripts use but without a per-step key or a
/// finalized-release guard.
/// </summary>
public class JournalingStoreTests
{
	[Fact]
	public void UnsetSlotReturnsNull()
	{
		using TempProject project = TempProject.Create();
		using FileSystemProjectStores stores = project.Open();

		Assert.Null( stores.InfoStore.GetJournalingStatement( JournalingSlot.PrepareJournal ) );
	}

	[Fact]
	public void StatementRoundTrips()
	{
		using TempProject project = TempProject.Create();
		using FileSystemProjectStores stores = project.Open();

		stores.InfoStore.SetJournalingStatement(
			JournalingSlot.PrepareJournal,
			"create table journal( release text not null )"
		);

		// SetJournalingStatement appends a trailing newline, matching JsonFiles.Write
		Assert.Equal(
			"create table journal( release text not null )\n",
			stores.InfoStore.GetJournalingStatement( JournalingSlot.PrepareJournal )
		);
	}

	[Fact]
	public void StatementFileLandsAtItsSlotName()
	{
		using TempProject project = TempProject.Create();
		using FileSystemProjectStores stores = project.Open();

		stores.InfoStore.SetJournalingStatement( JournalingSlot.DeployingRelease, "-- deploying a release" );

		Assert.True(
			File.Exists( Path.Combine( project.Root, "journaling", "OnDeployingRelease.sql" ) )
		);
	}

	[Fact]
	public void OverwriteReplacesTheStatement()
	{
		using TempProject project = TempProject.Create();
		using FileSystemProjectStores stores = project.Open();

		stores.InfoStore.SetJournalingStatement( JournalingSlot.PrepareJournal, "create table a" );
		stores.InfoStore.SetJournalingStatement( JournalingSlot.PrepareJournal, "create table b" );

		Assert.Equal(
			"create table b\n",
			stores.InfoStore.GetJournalingStatement( JournalingSlot.PrepareJournal )
		);
	}

	[Fact]
	public void MultiStatementTextSurvivesWithOneTrailingNewline()
	{
		using TempProject project = TempProject.Create();
		using FileSystemProjectStores stores = project.Open();

		const string text = "create table a\n;;\ncreate table b\n;;\ncreate table c";

		stores.InfoStore.SetJournalingStatement( JournalingSlot.PrepareJournal, text );

		Assert.Equal( text + "\n", stores.InfoStore.GetJournalingStatement( JournalingSlot.PrepareJournal ) );
	}

	/// <summary>
	/// Reading a statement back and writing it again must not change it — otherwise piping
	/// 'journaling print' into 'journaling set' grows a trailing line on every cycle.
	/// </summary>
	[Fact]
	public void WritingBackWhatWasReadChangesNothing()
	{
		using TempProject project = TempProject.Create();
		using FileSystemProjectStores stores = project.Open();

		stores.InfoStore.SetJournalingStatement( JournalingSlot.PrepareJournal, "create table a" );

		for( int cycle = 0; cycle < 3; cycle++ )
			stores.InfoStore.SetJournalingStatement(
				JournalingSlot.PrepareJournal,
				stores.InfoStore.GetJournalingStatement( JournalingSlot.PrepareJournal )!
			);

		Assert.Equal(
			"create table a\n",
			stores.InfoStore.GetJournalingStatement( JournalingSlot.PrepareJournal )
		);
	}
}