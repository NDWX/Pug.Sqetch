using Pug.Sqetch.Planning;
using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

namespace Pug.Sqetch.Tests.Planning.Stores.FileSystem;

/// <summary>
/// The store's edits: plan and release updates in one write each, and a step rename whose folder
/// move is the commit point, with what a rename cut short leaves behind repaired by a reindex.
/// </summary>
public sealed class EditStoreTests : IDisposable
{
	private readonly TempProject _project;
	private readonly FileSystemProjectStores _stores;

	public EditStoreTests()
	{
		_project = TempProject.Create();
		_stores = _project.Open();

		_stores.InfoStore.AddPlan( new ObjectDefinition( "orders", "" ), [ ], TestData.Context() );
		AddStep( "create-table" );
		AddStep( "add-index", "create-table" );
		AddStep( "seed", "add-index" );
	}

	public void Dispose()
	{
		_stores.Dispose();
		_project.Dispose();
	}

	private void AddStep( string name, params string[] dependencies )
		=> _stores.InfoStore.AddStep( "orders", new ObjectDefinition( name, "" ), dependencies, TestData.Context() );

	private string StepsDirectory => Path.Combine( _project.Root, "plans", "orders", "steps" );

	private string StepFile( string step ) => Path.Combine( StepsDirectory, step, "step.json" );

	private string[] DependenciesOf( string step ) => _stores.InfoStore.GetStepDependencies( "orders", step ).ToArray();

	private string[] StepNames()
		=> _stores.InfoStore.GetSteps( "orders" ).Keys.Order( StringComparer.Ordinal ).ToArray();

	[Fact]
	public void UpdatePlanWritesDescriptionAndDependenciesTogether()
	{
		_stores.InfoStore.AddPlan( new ObjectDefinition( "customers", "" ), [ ], TestData.Context() );

		_stores.InfoStore.UpdatePlan( new PlanDefinition( "orders", "orders and their lines", [ "customers" ] ) );

		using FileSystemProjectStores reopened = _project.Open();

		ProjectPlan plan = reopened.InfoStore.GetPlan( "orders" )!;

		Assert.Equal( "orders and their lines", plan.Definition.Description );
		Assert.Equal( [ "customers" ], ( (PlanDefinition)plan.Definition ).Dependencies );
		Assert.Equal( [ "orders" ], reopened.InfoStore.GetPlanDependants( "customers" ) );
	}

	[Fact]
	public void UpdateReleaseChangesTheDescriptionOfAnOpenReleaseOnly()
	{
		_stores.InfoStore.AddRelease( new ReleaseDefinition( "1.0", "", "" ), TestData.Context() );
		_stores.InfoStore.AddReleasePlan( "1.0", "orders", TestData.Context() );

		_stores.InfoStore.UpdateRelease( new ObjectDefinition( "1.0", "first cut" ) );

		Assert.Equal( "first cut", _stores.InfoStore.GetRelease( "1.0" )!.Definition.Description );

		_stores.InfoStore.SetReleaseContext( "1.0", TestData.Context() );

		Assert.Throws<ReleaseFinalizedException>( () =>
			_stores.InfoStore.UpdateRelease( new ObjectDefinition( "1.0", "changed" ) )
		);

		Assert.Throws<UnknownReleaseException>( () =>
			_stores.InfoStore.UpdateRelease( new ObjectDefinition( "2.0", "" ) )
		);

		Assert.Equal( "first cut", _stores.InfoStore.GetRelease( "1.0" )!.Definition.Description );
	}

	[Fact]
	public void RenameStepMovesTheFolderAndRepointsItsDependants()
	{
		File.WriteAllText( Path.Combine( StepsDirectory, "add-index", "deploy.sql" ), "create index x;" );

		_stores.InfoStore.RenameStep( "orders", "add-index", "index-orders" );

		Assert.False( Directory.Exists( Path.Combine( StepsDirectory, "add-index" ) ) );
		Assert.Equal( "create index x;", File.ReadAllText( Path.Combine( StepsDirectory, "index-orders", "deploy.sql" ) ) );
		Assert.Contains( "\"index-orders\"", File.ReadAllText( StepFile( "index-orders" ) ) );
		Assert.Equal( [ "create-table", "index-orders", "seed" ], StepNames() );
		Assert.Equal( [ "create-table" ], DependenciesOf( "index-orders" ) );
		Assert.Equal( [ "index-orders" ], DependenciesOf( "seed" ) );
	}

	[Fact]
	public void RenameStepRefusesWhatItCannotDo()
	{
		Assert.Throws<UnknownStepException>( () => _stores.InfoStore.RenameStep( "orders", "missing", "other" ) );
		Assert.Throws<DuplicateStepNameException>( () => _stores.InfoStore.RenameStep( "orders", "seed", "ADD-INDEX" ) );

		// a folder holding only scripts is not a step, but is in the way
		Directory.CreateDirectory( Path.Combine( StepsDirectory, "leftover" ) );

		Assert.Throws<ProjectStoreException>( () => _stores.InfoStore.RenameStep( "orders", "seed", "leftover" ) );
		Assert.Equal( [ "add-index", "create-table", "seed" ], StepNames() );
		Assert.Equal( [ "add-index" ], DependenciesOf( "seed" ) );
	}

	/// <summary>
	/// Cut short before the folder moved, a dependant names the new step beside the old one: the
	/// new name matches no step, so nothing reads differently, and running the rename again
	/// finishes it.
	/// </summary>
	[Fact]
	public void ARenameCutShortBeforeTheFolderMovedCanBeRunAgain()
	{
		WriteDependencies( "seed", "add-index", "index-orders" );

		Assert.Equal( [ "add-index", "create-table", "seed" ], StepNames() );

		_stores.InfoStore.RenameStep( "orders", "add-index", "index-orders" );

		Assert.Equal( [ "index-orders" ], DependenciesOf( "seed" ) );
		Assert.Equal( [ "create-table", "index-orders", "seed" ], StepNames() );
	}

	/// <summary>
	/// Cut short after the folder moved: the folder names the step, so it reads as renamed, and a
	/// reindex realigns step.json and drops the old name from its dependant.
	/// </summary>
	[Fact]
	public void ARenameCutShortAfterTheFolderMovedIsRepairedByReindex()
	{
		WriteDependencies( "seed", "add-index", "index-orders" );
		Directory.Move( Path.Combine( StepsDirectory, "add-index" ), Path.Combine( StepsDirectory, "index-orders" ) );

		Assert.Equal( [ "create-table", "index-orders", "seed" ], StepNames() );
		Assert.Equal( "index-orders", _stores.InfoStore.GetStep( "orders", "index-orders" )!.Definition.Name );

		_stores.Reindex();

		Assert.Contains( "\"index-orders\"", File.ReadAllText( StepFile( "index-orders" ) ) );
		Assert.DoesNotContain( "add-index", File.ReadAllText( StepFile( "index-orders" ) ) );
		Assert.Equal( [ "index-orders" ], DependenciesOf( "seed" ) );
	}

	[Fact]
	public void ReindexLeavesConsistentStepsUntouched()
	{
		string[] files = Directory.GetFiles( StepsDirectory, "step.json", SearchOption.AllDirectories );
		Dictionary<string, (string Text, DateTime Written)> before = files.ToDictionary(
			x => x,
			x => ( File.ReadAllText( x ), File.GetLastWriteTimeUtc( x ) )
		);

		_stores.Reindex();

		foreach( string file in files )
		{
			Assert.Equal( before[file].Text, File.ReadAllText( file ) );
			Assert.Equal( before[file].Written, File.GetLastWriteTimeUtc( file ) );
		}
	}

	private void WriteDependencies( string step, params string[] dependencies )
		=> _stores.InfoStore.SetStepDependencies( "orders", step, dependencies );
}
