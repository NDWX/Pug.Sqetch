using System.Reflection;
using System.Runtime.ExceptionServices;

using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStore;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;
using Pug.Sqetch.Tests.Planning.Stores.FileSystem;

namespace Pug.Sqetch.Planning.Tests;

/// <summary>
/// The edits: <see cref="IProject.UpdatePlan"/>, <see cref="IProject.UpdateRelease"/>,
/// <see cref="IProject.UpdateStep"/>, <see cref="IProject.RenameStep"/> and
/// <see cref="IProject.MoveStep"/>. Every refusal leaves the project as it was; a move is a
/// linked-list move whose every write leaves the plan acyclic.
/// </summary>
public sealed class EditCommandsTests : IDisposable
{
	private readonly TempProject _temp;
	private readonly FileSystemProjectStores _stores;
	private readonly IProject _project;

	public EditCommandsTests()
	{
		_temp = TempProject.Create();
		_stores = _temp.Open();
		_project = Open( _stores.InfoStore );
	}

	public void Dispose()
	{
		_project.Dispose();
		_stores.Dispose();
		_temp.Dispose();
	}

	private IProject Open( IProjectInfoStore infoStore )
		=> ProjectFactory.Create(
			infoStore,
			_stores.ScriptsStore,
			new ReleaseChainDependencyDeterminator( infoStore ),
			TestData.User
		);

	private void Plan( string name, params string[] dependencies )
		=> _project.Add( new PlanDefinition( name, "", dependencies ) );

	private void Step( string name, string? dependency = null )
		=> _project.Add( new StepDefinition( "p", name, "", dependency is null ? [ ] : [ dependency ] ), "p" );

	/// <summary>Plan <c>p</c> with steps created in the order given, each requiring the one before.</summary>
	private void Chain( params string[] steps )
	{
		Plan( "p" );

		for( int index = 0; index < steps.Length; index++ )
			Step( steps[index], index == 0 ? null : steps[index - 1] );
	}

	private string[] Steps() => _project.GetSteps( "p" ).Select( x => x.Definition.Name ).ToArray();

	private string Requires( string step ) => string.Join( ", ", _stores.InfoStore.GetStepDependencies( "p", step ) );

	private string[] PlanDependencies( string plan )
		=> ( (PlanDefinition)_stores.InfoStore.GetPlan( plan )!.Definition ).Dependencies.ToArray();

	private void Finalized( string plan )
	{
		_project.CreateRelease( new ReleaseDefinition( "1.0", "", "" ), [ plan ] );
		_project.FinalizeRelease( "1.0" );
	}

	// ---- plans

	[Fact]
	public void UpdatePlanChangesDescriptionAndDependencies()
	{
		Plan( "base" );
		Plan( "other" );
		Plan( "p", "base" );

		_project.UpdatePlan( "p", "new description", null );

		Assert.Equal( "new description", _stores.InfoStore.GetPlan( "p" )!.Definition.Description );
		Assert.Equal( [ "base" ], PlanDependencies( "p" ) );

		_project.UpdatePlan( "p", null, [ "OTHER", "base", "other" ] );

		Assert.Equal( "new description", _stores.InfoStore.GetPlan( "p" )!.Definition.Description );
		Assert.Equal( [ "other", "base" ], PlanDependencies( "p" ) );

		_project.UpdatePlan( "p", null, [ ] );

		Assert.Empty( PlanDependencies( "p" ) );
	}

	[Fact]
	public void UpdatePlanRefusesACycle()
	{
		Plan( "a" );
		Plan( "b", "a" );
		Plan( "c", "b" );

		PlanDependencyCycleException transitive = Assert.Throws<PlanDependencyCycleException>( () =>
			_project.UpdatePlan( "a", null, [ "c" ] )
		);

		Assert.Equal( "a", transitive.Plan );
		Assert.Equal( [ "c", "b", "a" ], transitive.Path );

		PlanDependencyCycleException self = Assert.Throws<PlanDependencyCycleException>( () =>
			_project.UpdatePlan( "a", "changed", [ "A" ] )
		);

		Assert.Equal( [ "a" ], self.Path );
		Assert.Empty( PlanDependencies( "a" ) );
		Assert.Equal( "", _stores.InfoStore.GetPlan( "a" )!.Definition.Description );
	}

	[Fact]
	public void UpdatePlanKeepsAReleasedPlansDependenciesInItsReleaseOrEarlier()
	{
		Plan( "earlier" );
		Plan( "later" );
		Plan( "pending" );
		Plan( "p" );
		_project.CreateRelease( new ReleaseDefinition( "1.0", "", "" ), [ "earlier" ] );
		_project.CreateRelease( new ReleaseDefinition( "1.1", "", "1.0" ), [ "p" ] );
		_project.CreateRelease( new ReleaseDefinition( "1.2", "", "1.1" ), [ "later" ] );

		MissingPlanDependenciesException missing = Assert.Throws<MissingPlanDependenciesException>( () =>
			_project.UpdatePlan( "p", null, [ "earlier", "later", "pending" ] )
		);

		Assert.Equal( [ "later", "pending" ], missing.MissingDependencies.Keys.Order() );
		Assert.Empty( PlanDependencies( "p" ) );

		_project.UpdatePlan( "p", null, [ "earlier" ] );

		Assert.Equal( [ "earlier" ], PlanDependencies( "p" ) );
	}

	[Fact]
	public void UpdatePlanRefusesUnknownPlansAndFinalizedOnes()
	{
		Plan( "p" );

		Assert.Throws<UnknownPlanException>( () => _project.UpdatePlan( "missing", "x", null ) );
		Assert.Throws<UnknownPlanException>( () => _project.UpdatePlan( "p", null, [ "missing" ] ) );

		Finalized( "p" );

		Assert.Throws<PlanFinalizedException>( () => _project.UpdatePlan( "p", "changed", null ) );
		Assert.Equal( "", _stores.InfoStore.GetPlan( "p" )!.Definition.Description );
	}

	// ---- releases

	[Fact]
	public void UpdateReleaseChangesAnOpenReleasesDescription()
	{
		Plan( "p" );
		_project.CreateRelease( new ReleaseDefinition( "1.0", "", "" ), [ "p" ] );

		_project.UpdateRelease( "1.0", "first cut" );

		Assert.Equal( "first cut", _stores.InfoStore.GetRelease( "1.0" )!.Definition.Description );

		_project.FinalizeRelease( "1.0" );

		Assert.Throws<ReleaseFinalizedException>( () => _project.UpdateRelease( "1.0", "changed" ) );
		Assert.Throws<UnknownReleaseException>( () => _project.UpdateRelease( "2.0", "x" ) );
		Assert.Equal( "first cut", _stores.InfoStore.GetRelease( "1.0" )!.Definition.Description );
	}

	// ---- steps

	[Fact]
	public void AStepRequiresAtMostOneStep()
	{
		Chain( "a", "b" );

		Assert.Throws<ArgumentException>( () =>
			_project.Add( new StepDefinition( "p", "c", "", [ "a", "b" ] ), "p" )
		);

		Assert.Equal( [ "a", "b" ], Steps() );
	}

	[Fact]
	public void UpdateStepChangesItsDescription()
	{
		Chain( "a" );

		_project.UpdateStep( "p", "A", "creates the table" );

		Assert.Equal( "creates the table", _stores.InfoStore.GetStep( "p", "a" )!.Definition.Description );
		Assert.Throws<UnknownStepException>( () => _project.UpdateStep( "p", "missing", "x" ) );

		Finalized( "p" );

		Assert.Throws<PlanFinalizedException>( () => _project.UpdateStep( "p", "a", "changed" ) );
	}

	[Fact]
	public void RenameStepKeepsItsPlaceAndDependants()
	{
		Chain( "a", "b", "c" );

		// the engine registers a step without script files; the CLI seeds them
		foreach( string script in new[] { "deploy.sql", "verify.sql", "rollback.sql" } )
			File.WriteAllText( Path.Combine( _temp.Root, "plans", "p", "steps", "b", script ), script );

		_project.RenameStep( "p", "b", "middle" );

		Assert.Equal( [ "a", "middle", "c" ], Steps() );
		Assert.Equal( "a", Requires( "middle" ) );
		Assert.Equal( "middle", Requires( "c" ) );

		using StepScripts scripts = _project.GetStepScripts( "p", "middle" );
		using StreamReader deploy = new( scripts.DeployScript! );

		Assert.Equal( "deploy.sql", deploy.ReadToEnd() );
	}

	[Fact]
	public void RenameStepRefusesATakenNameACaseOnlyChangeAndAFinalizedPlan()
	{
		Chain( "a", "b" );

		Assert.Throws<DuplicateStepNameException>( () => _project.RenameStep( "p", "b", "A" ) );
		Assert.Throws<ArgumentException>( () => _project.RenameStep( "p", "b", "B" ) );
		Assert.Throws<UnknownStepException>( () => _project.RenameStep( "p", "missing", "c" ) );

		_project.RenameStep( "p", "b", "b" );

		Finalized( "p" );

		Assert.Throws<PlanFinalizedException>( () => _project.RenameStep( "p", "b", "c" ) );
		Assert.Equal( [ "a", "b" ], Steps() );
	}

	// ---- moves

	[Fact]
	public void MovingAfterSplicesTheChain()
	{
		Chain( "a", "b", "c", "d" );

		IReadOnlyList<StepDependencyChange> changes = _project.MoveStep( "p", "b", StepPlacement.After, "d" );

		Assert.Equal( [ "a", "c", "d", "b" ], Steps() );
		Assert.Equal( [ new StepDependencyChange( "c", "a", "b" ), new StepDependencyChange( "b", "d", "a" ) ], changes );
	}

	[Fact]
	public void MovingBeforeSplicesTheChain()
	{
		Chain( "a", "b", "c", "d" );

		IReadOnlyList<StepDependencyChange> changes = _project.MoveStep( "p", "d", StepPlacement.Before, "b" );

		Assert.Equal( [ "a", "d", "b", "c" ], Steps() );
		Assert.Equal( [ new StepDependencyChange( "d", "a", "c" ), new StepDependencyChange( "b", "d", "a" ) ], changes );
	}

	[Fact]
	public void MovingAStepAfterItsOwnDependantMakesNoCycle()
	{
		Chain( "a", "b", "c" );

		_project.MoveStep( "p", "a", StepPlacement.After, "c" );

		Assert.Equal( [ "b", "c", "a" ], Steps() );
		Assert.Equal( "", Requires( "b" ) );
		Assert.Equal( "c", Requires( "a" ) );
	}

	[Fact]
	public void MovingAStepBeforeItsOwnDependencyMakesNoCycle()
	{
		Chain( "a", "b", "c" );

		_project.MoveStep( "p", "c", StepPlacement.Before, "a" );

		Assert.Equal( [ "c", "a", "b" ], Steps() );
		Assert.Equal( "", Requires( "c" ) );
		Assert.Equal( "c", Requires( "a" ) );
	}

	[Fact]
	public void MovingAfterAStepWithSeveralFollowersPutsTheStepBeforeAllOfThem()
	{
		Plan( "p" );
		Step( "a" );
		Step( "b", "a" );
		Step( "c", "a" );
		Step( "d" );

		_project.MoveStep( "p", "d", StepPlacement.After, "a" );

		Assert.Equal( [ "a", "d", "b", "c" ], Steps() );
		Assert.Equal( "d", Requires( "b" ) );
		Assert.Equal( "d", Requires( "c" ) );
	}

	[Fact]
	public void MovingToTheEndsOfTheChain()
	{
		Chain( "a", "b", "c" );

		_project.MoveStep( "p", "b", StepPlacement.Before, "a" );
		Assert.Equal( [ "b", "a", "c" ], Steps() );

		_project.MoveStep( "p", "b", StepPlacement.After, "c" );
		Assert.Equal( [ "a", "c", "b" ], Steps() );
	}

	[Theory]
	[InlineData( StepPlacement.After, "a" )]
	[InlineData( StepPlacement.Before, "c" )]
	public void AMoveWithNothingToChangeWritesNothing( StepPlacement placement, string anchor )
	{
		Chain( "a", "b", "c" );

		string[] files = Directory.GetFiles( _temp.Root, "step.json", SearchOption.AllDirectories );
		Dictionary<string, DateTime> written = files.ToDictionary( x => x, File.GetLastWriteTimeUtc );

		Assert.Empty( _project.MoveStep( "p", "b", placement, anchor ) );

		foreach( string file in files )
			Assert.Equal( written[file], File.GetLastWriteTimeUtc( file ) );
	}

	[Fact]
	public void ARefusedMoveChangesNothing()
	{
		Chain( "a", "b", "c" );

		Assert.Throws<UnknownStepException>( () => _project.MoveStep( "p", "a", StepPlacement.After, "missing" ) );
		Assert.Throws<UnknownStepException>( () => _project.MoveStep( "p", "missing", StepPlacement.After, "a" ) );
		Assert.Throws<ArgumentException>( () => _project.MoveStep( "p", "a", StepPlacement.After, "A" ) );
		Assert.Throws<UnknownPlanException>( () => _project.MoveStep( "missing", "a", StepPlacement.After, "b" ) );

		Finalized( "p" );

		Assert.Throws<PlanFinalizedException>( () => _project.MoveStep( "p", "a", StepPlacement.After, "c" ) );
		Assert.Equal( [ "a", "b", "c" ], Steps() );
	}

	public static TheoryData<string, StepPlacement, string> Moves => new()
	{
		{ "b", StepPlacement.After, "d" },
		{ "d", StepPlacement.Before, "b" },
		{ "a", StepPlacement.After, "d" },
		{ "d", StepPlacement.Before, "a" },
		{ "b", StepPlacement.After, "c" }
	};

	/// <summary>
	/// A move cut short after any of its writes leaves the plan acyclic, and running it again ends
	/// where an uninterrupted move ends.
	/// </summary>
	[Theory]
	[MemberData( nameof(Moves) )]
	public void AMoveCutShortLeavesAnAcyclicPlanAndFinishesWhenRunAgain( string step, StepPlacement placement, string anchor )
	{
		Chain( "a", "b", "c", "d" );
		Step( "e", "b" );

		Dictionary<string, string> initial = Dependencies();

		FailingStore counting = FailingStore.Wrap( _stores.InfoStore, int.MaxValue, out IProjectInfoStore countingStore );

		using( IProject uninterrupted = Open( countingStore ) )
			uninterrupted.MoveStep( "p", step, placement, anchor );

		Dictionary<string, string> expected = Dependencies();

		Assert.NotEqual( initial, expected );
		Assert.True( counting.Writes > 0 );

		for( int allowed = 0; allowed < counting.Writes; allowed++ )
		{
			Restore( initial );

			FailingStore.Wrap( _stores.InfoStore, allowed, out IProjectInfoStore failingStore );

			using( IProject failing = Open( failingStore ) )
				Assert.Throws<IOException>( () => failing.MoveStep( "p", step, placement, anchor ) );

			AssertAcyclic();

			_project.MoveStep( "p", step, placement, anchor );

			Assert.Equal( expected, Dependencies() );
		}
	}

	private Dictionary<string, string> Dependencies()
		=> _stores.InfoStore.GetSteps( "p" ).Keys.ToDictionary( x => x, Requires );

	private void Restore( Dictionary<string, string> dependencies )
	{
		foreach( (string step, string dependency) in dependencies )
			_stores.InfoStore.SetStepDependencies( "p", step, dependency.Length == 0 ? [ ] : [ dependency ] );
	}

	private void AssertAcyclic()
	{
		foreach( string start in _stores.InfoStore.GetSteps( "p" ).Keys )
		{
			HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );

			for( string? at = start; at is not null && at.Length != 0; )
			{
				Assert.True( seen.Add( at ), $"cycle through '{start}'" );

				at = _stores.InfoStore.GetStepDependencies( "p", at ).SingleOrDefault();
			}
		}
	}

	/// <summary>
	/// The real store, failing the dependency write after <c>allowed</c> of them, as a crash or a
	/// full disk would. It never disposes the store it wraps, which the test still uses.
	/// </summary>
	public class FailingStore : DispatchProxy
	{
		private IProjectInfoStore _inner = null!;
		private int _allowed;

		/// <summary>The dependency writes that went through.</summary>
		public int Writes { get; private set; }

		public static FailingStore Wrap( IProjectInfoStore inner, int allowed, out IProjectInfoStore store )
		{
			store = Create<IProjectInfoStore, FailingStore>();
			FailingStore failing = (FailingStore)(object)store;

			failing._inner = inner;
			failing._allowed = allowed;

			return failing;
		}

		protected override object? Invoke( MethodInfo? method, object?[]? args )
		{
			if( method!.Name == nameof(IDisposable.Dispose) )
				return null;

			if( method.Name == nameof(IProjectInfoStore.SetStepDependencies) )
			{
				if( Writes == _allowed )
					throw new IOException( "cut short" );

				Writes++;
			}

			try
			{
				return method.Invoke( _inner, args );
			}
			catch( TargetInvocationException exception )
			{
				ExceptionDispatchInfo.Throw( exception.InnerException! );

				throw;
			}
		}
	}
}
