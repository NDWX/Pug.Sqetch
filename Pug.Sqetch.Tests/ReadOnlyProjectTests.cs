using Pug.Sqetch.Models;
using Pug.Sqetch.ProjectInfoStores.FileSystem;
using Pug.Sqetch.Tests.Stores.FileSystem;

namespace Pug.Sqetch.Tests;

/// <summary>
/// <see cref="ProjectFactory.CreateReadOnly"/>: a project opened without a user, which is how
/// bundling reads a project in a pipeline that has no project user configured.
/// </summary>
public class ReadOnlyProjectTests
{
	[Fact]
	public void ReadsWhatAWritableProjectWrote()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();

		using( IProject writable = ProjectFactory.Create(
					stores.InfoStore, stores.ScriptsStore,
					new ReleaseChainDependencyDeterminator( stores.InfoStore ), TestData.User ) )
		{
			writable.Add( new PlanDefinition( "table", "Create table", [] ) );
			writable.Add( new PlanDefinition( "index", "Create index", ["table"] ) );
		}

		using FileSystemProjectStores reopened = temp.Open();
		using IReadOnlyProject project = CreateReadOnly( reopened );

		Assert.Equal( new ProjectDefinition( "test-project", "Test project", "postgres" ), project.GetDefinition() );
		Assert.Equal(
			["table", "index"],
			project.GetPlans( new PlanSearchCriteria() ).Select( x => x.Definition.Name ).ToArray() );
	}

	[Fact]
	public void RefusesAWriteThroughACastAndRecordsNothing()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();
		using IReadOnlyProject project = CreateReadOnly( stores );

		// handed out as IReadOnlyProject; a caller casting it back has a bug, which must not
		// write a change with no one recorded against it
		IProject writable = Assert.IsAssignableFrom<IProject>( project );

		Assert.Throws<InvalidOperationException>(
			() => writable.Add( new PlanDefinition( "table", "Create table", [] ) ) );

		Assert.Empty( project.GetPlans( new PlanSearchCriteria() ) );
	}

	[Fact]
	public void AWritableProjectStillRequiresAUser()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();

		Assert.Throws<ArgumentNullException>(
			() => ProjectFactory.Create(
				stores.InfoStore, stores.ScriptsStore,
				new ReleaseChainDependencyDeterminator( stores.InfoStore ), null! ) );
	}

	private static IReadOnlyProject CreateReadOnly( FileSystemProjectStores stores )
		=> ProjectFactory.CreateReadOnly(
			stores.InfoStore, stores.ScriptsStore, new ReleaseChainDependencyDeterminator( stores.InfoStore ) );
}
