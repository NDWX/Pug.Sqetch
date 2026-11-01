using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;
using Pug.Sqetch.Tests.Planning.Stores.FileSystem;

namespace Pug.Sqetch.Planning.Tests;

/// <summary>
/// Pins the project layer's journaling contract: <see cref="IProject.SetJournalingStatement"/>
/// validates before the store ever sees the text, and
/// <see cref="IReadOnlyProject.VerifyJournalingStatements"/> reports every missing slot at once,
/// unlike <see cref="IReadOnlyProject.VerifyStepScripts"/> which stops at the first.
/// </summary>
public class JournalingProjectTests
{
	private static IProject CreateProject( FileSystemProjectStores stores )
		=> ProjectFactory.Create(
			stores.InfoStore, stores.ScriptsStore,
			new ReleaseChainDependencyDeterminator( stores.InfoStore ), TestData.User );

	[Fact]
	public void StatementRoundTrips()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = CreateProject( stores );

		Assert.Null( project.GetJournalingStatement( JournalingSlot.PrepareJournal ) );

		project.SetJournalingStatement( JournalingSlot.PrepareJournal, "create table journal( release text )" );

		Assert.Equal(
			"create table journal( release text )\n",
			project.GetJournalingStatement( JournalingSlot.PrepareJournal ) );
	}

	[Fact]
	public void AMalformedStatementIsRejectedBeforeItReachesTheStore()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = CreateProject( stores );

		// a query slot may hold only one statement
		Assert.Throws<ArgumentException>(
			() => project.SetJournalingStatement( JournalingSlot.GetLatestRelease, "select 1\n;;\nselect 2" ) );

		Assert.Null( project.GetJournalingStatement( JournalingSlot.GetLatestRelease ) );
		Assert.False( Directory.Exists( Path.Combine( temp.Root, "journaling" ) ) );
	}

	[Fact]
	public void VerifyJournalingStatementsNamesEveryMissingSlot()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = CreateProject( stores );

		project.SetJournalingStatement( JournalingSlot.PrepareJournal, "-- prepare" );
		project.SetJournalingStatement( JournalingSlot.GetLatestRelease, "select 1" );

		MissingJournalingStatementsException error = Assert.Throws<MissingJournalingStatementsException>(
			project.VerifyJournalingStatements );

		Assert.Equal(
			JournalingSlots.All.Where( slot => slot is not JournalingSlot.PrepareJournal and not JournalingSlot.GetLatestRelease ),
			error.Slots );
	}

	[Fact]
	public void VerifyJournalingStatementsPassesOnceAllFifteenAreSet()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = CreateProject( stores );

		foreach( JournalingSlot slot in JournalingSlots.All )
			project.SetJournalingStatement( slot, $"-- {slot}" );

		project.VerifyJournalingStatements();
	}
}
