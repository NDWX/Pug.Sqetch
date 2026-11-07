using Pug.Sqetch.Deployment;

using static Pug.Sqetch.Tests.Deployment.Lineages;

namespace Pug.Sqetch.Tests.Deployment;

/// <summary>
/// <see cref="RollbackPlanner"/> on its own — no database, no listener. The journal decides what is
/// deployed, by membership; the groups' order, reversed, decides the sequence.
/// </summary>
public class RollbackPlannerTests
{
	private static IReadOnlyList<ReleaseGroup> ThreeReleases()
		=> Lineage(
			[ Release( "r1" ), Release( "r2", "r1" ), Release( "r3", "r2" ) ],
			[ Plan( "a", "r1", "s" ), Plan( "b", "r2", "s" ), Plan( "c", "r2", "s" ), Plan( "d", "r3", "s" ) ]
		);

	private static JournaledRelease Done( string release ) => new( release, Completed: true );

	private static JournaledRelease Partway( string release ) => new( release, Completed: false );

	private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> Deployed(
		params (string Release, string[] Plans)[] releases
	)
		=> releases.ToDictionary( x => x.Release, x => (IReadOnlyCollection<string>)x.Plans );

	private static string[] Undone( RollbackForecast forecast )
		=> forecast.Releases
			.SelectMany( release => release.Plans.Select( plan => $"{release.Group.Name}/{plan.Name}" ) )
			.ToArray();

	private static string[] Names( IEnumerable<ReleaseGroup> groups ) => groups.Select( group => group.Name ).ToArray();

	// ---------------------------------------------------------------- the target

	[Fact]
	public void ToReleaseNeedsAName()
		=> Assert.ThrowsAny<ArgumentException>( () => RollbackTarget.ToRelease( "" ) );

	[Fact]
	public void OfPlansNeedsAtLeastOnePlan()
		=> Assert.Throws<ArgumentException>( () => RollbackTarget.OfPlans( [ ] ) );

	[Fact]
	public void OfPlansDropsDuplicates()
		=> Assert.Equal( [ "a", "b" ], RollbackTarget.OfPlans( [ "a", "b", "a" ] ).Plans );

	// ---------------------------------------------------------------- ToRelease

	[Fact]
	public void ToReleaseUndoesEverythingAfterItNewestFirst()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			ThreeReleases(),
			Done( "r3" ),
			Deployed( ( "r3", [ "d" ] ), ( "r2", [ "b", "c" ] ) ),
			RollbackTarget.ToRelease( "r1" )
		);

		Assert.False( forecast.NothingToRollBack );
		Assert.Equal( Done( "r3" ), forecast.Journaled );
		Assert.Equal( [ "r3", "r2" ], forecast.Releases.Select( release => release.Group.Name ) );
		Assert.Equal( [ "r3/d", "r2/b", "r2/c" ], Undone( forecast ) );
	}

	[Fact]
	public void ToReleaseKeepsTheNamedReleaseWhole()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			ThreeReleases(),
			Done( "r3" ),
			Deployed( ( "r3", [ "d" ] ) ),
			RollbackTarget.ToRelease( "r2" )
		);

		Assert.Equal( [ "r3/d" ], Undone( forecast ) );
	}

	[Fact]
	public void PlansAreListedInBundleOrderWhateverOrderTheJournalHoldsThem()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			ThreeReleases(),
			Done( "r2" ),
			Deployed( ( "r2", [ "c", "b" ] ) ),
			RollbackTarget.ToRelease( "r1" )
		);

		Assert.Equal( [ "r2/b", "r2/c" ], Undone( forecast ) );
	}

	[Fact]
	public void OnlyPlansTheJournalHoldsAreUndone()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			ThreeReleases(),
			Partway( "r2" ),
			Deployed( ( "r2", [ "c" ] ) ),
			RollbackTarget.ToRelease( "r1" )
		);

		Assert.Equal( [ "r2/c" ], Undone( forecast ) );
	}

	[Fact]
	public void AnIncompleteLatestReleaseIsUndoneToo()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			ThreeReleases(),
			Partway( "r3" ),
			Deployed( ( "r3", [ "d" ] ), ( "r2", [ "b", "c" ] ) ),
			RollbackTarget.ToRelease( "r1" )
		);

		Assert.Equal( [ "r3/d", "r2/b", "r2/c" ], Undone( forecast ) );
	}

	[Fact]
	public void AnIncompleteReleaseWithNoPlansIsStillAReleaseToUndo()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			ThreeReleases(),
			Partway( "r3" ),
			Deployed( ( "r3", [ ] ), ( "r2", [ "b", "c" ] ) ),
			RollbackTarget.ToRelease( "r1" )
		);

		Assert.False( forecast.NothingToRollBack );
		Assert.Equal( [ "r3", "r2" ], forecast.Releases.Select( release => release.Group.Name ) );
		Assert.Empty( forecast.Releases[0].Plans );
		Assert.Equal( [ "r2/b", "r2/c" ], Undone( forecast ) );
	}

	[Fact]
	public void AReleaseBehindTheLatestIsReadAsDeployedOnlyForThePlansTheJournalHolds()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			ThreeReleases(),
			Done( "r3" ),
			Deployed( ( "r3", [ "d" ] ), ( "r2", [ "b" ] ) ),
			RollbackTarget.ToRelease( "r1" )
		);

		Assert.Equal( [ "r3/d", "r2/b" ], Undone( forecast ) );
	}

	// ---------------------------------------------------------------- nothing to roll back

	[Fact]
	public void NothingJournaledIsNothingToRollBack()
	{
		foreach( RollbackTarget target in new[] { RollbackTarget.All, RollbackTarget.ToRelease( "r1" ), RollbackTarget.OfPlans( [ "a" ] ) } )
		{
			RollbackForecast forecast = RollbackPlanner.Plan( ThreeReleases(), null, Deployed(), target );

			Assert.True( forecast.NothingToRollBack );
			Assert.Null( forecast.Journaled );
		}
	}

	[Fact]
	public void ToTheLatestReleaseIsNothingToRollBack()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			ThreeReleases(),
			Done( "r2" ),
			Deployed(),
			RollbackTarget.ToRelease( "r2" )
		);

		Assert.True( forecast.NothingToRollBack );
	}

	[Fact]
	public void ToAReleaseAfterTheLatestIsNothingToRollBack()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			ThreeReleases(),
			Done( "r1" ),
			Deployed(),
			RollbackTarget.ToRelease( "r3" )
		);

		Assert.True( forecast.NothingToRollBack );
		Assert.Empty( RollbackPlanner.ReleasesToRollBack( ThreeReleases(), Done( "r1" ), RollbackTarget.ToRelease( "r3" ) ) );
	}

	[Fact]
	public void PlansAlreadyUndoneAreNothingToRollBack()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			ThreeReleases(),
			Partway( "r2" ),
			Deployed( ( "r2", [ "b" ] ) ),
			RollbackTarget.OfPlans( [ "c" ] )
		);

		Assert.True( forecast.NothingToRollBack );
	}

	// ---------------------------------------------------------------- All

	[Fact]
	public void AllUndoesTheWholeLineageNewestFirst()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			ThreeReleases(),
			Done( "r3" ),
			Deployed( ( "r3", [ "d" ] ), ( "r2", [ "b", "c" ] ), ( "r1", [ "a" ] ) ),
			RollbackTarget.All
		);

		Assert.Equal( [ "r3/d", "r2/b", "r2/c", "r1/a" ], Undone( forecast ) );
		Assert.Equal( [ "r3", "r2", "r1" ], forecast.Releases.Select( release => release.Group.Name ) );
	}

	[Fact]
	public void AllFromAMiddleReleaseUndoesItAndEveryOneBefore()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			ThreeReleases(),
			Partway( "r2" ),
			Deployed( ( "r2", [ "b" ] ), ( "r1", [ "a" ] ) ),
			RollbackTarget.All
		);

		Assert.Equal( [ "r2/b", "r1/a" ], Undone( forecast ) );
	}

	[Fact]
	public void AllIsRefusedForAContinuationBundle()
	{
		IReadOnlyList<ReleaseGroup> continuation = ThreeReleases().Skip( 1 ).ToList();

		Assert.Throws<IncompatibleBundleException>( () => RollbackPlanner.Plan(
				continuation,
				Done( "r3" ),
				Deployed( ( "r3", [ "d" ] ), ( "r2", [ "b", "c" ] ) ),
				RollbackTarget.All
			)
		);
	}

	// ---------------------------------------------------------------- the unreleased-plans pseudo-release

	private static IReadOnlyList<ReleaseGroup> WithUnreleased()
		=> Lineage(
			[ Release( "r1" ), Release( "r2", "r1" ) ],
			[ Plan( "a", "r1", "s" ), Plan( "b", "r2", "s" ), Plan( "x", "", "s" ), Plan( "y", "", "s" ) ]
		);

	[Fact]
	public void TheUnreleasedPlansAreUndoneFirst()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			WithUnreleased(),
			Done( "" ),
			Deployed( ( "", [ "x", "y" ] ), ( "r2", [ "b" ] ), ( "r1", [ "a" ] ) ),
			RollbackTarget.All
		);

		Assert.Equal( [ "", "r2", "r1" ], forecast.Releases.Select( release => release.Group.Name ) );
		Assert.Equal( [ "/x", "/y", "r2/b", "r1/a" ], Undone( forecast ) );
	}

	[Fact]
	public void ToTheLastRealReleaseUndoesOnlyTheUnreleasedPlans()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			WithUnreleased(),
			Partway( "" ),
			Deployed( ( "", [ "x" ] ) ),
			RollbackTarget.ToRelease( "r2" )
		);

		Assert.Equal( [ "/x" ], Undone( forecast ) );
	}

	[Fact]
	public void SomePlansOfThePseudoReleaseCanBeUndone()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			WithUnreleased(),
			Done( "" ),
			Deployed( ( "", [ "x", "y" ] ) ),
			RollbackTarget.OfPlans( [ "y" ] )
		);

		Assert.Equal( [ "/y" ], Undone( forecast ) );
	}

	// ---------------------------------------------------------------- continuation bundles

	[Fact]
	public void ToTheReleaseTheBundleContinuesFromUndoesEverythingTheBundleHolds()
	{
		IReadOnlyList<ReleaseGroup> continuation = ThreeReleases().Skip( 1 ).ToList();

		RollbackForecast forecast = RollbackPlanner.Plan(
			continuation,
			Done( "r3" ),
			Deployed( ( "r3", [ "d" ] ), ( "r2", [ "b", "c" ] ) ),
			RollbackTarget.ToRelease( "r1" )
		);

		Assert.Equal( [ "r3/d", "r2/b", "r2/c" ], Undone( forecast ) );
	}

	[Fact]
	public void ToAReleaseInsideAContinuationBundleStopsThere()
	{
		IReadOnlyList<ReleaseGroup> continuation = ThreeReleases().Skip( 1 ).ToList();

		RollbackForecast forecast = RollbackPlanner.Plan(
			continuation,
			Done( "r3" ),
			Deployed( ( "r3", [ "d" ] ) ),
			RollbackTarget.ToRelease( "r2" )
		);

		Assert.Equal( [ "r3/d" ], Undone( forecast ) );
	}

	[Fact]
	public void ADatabaseAtTheContinuationBaseHasNothingToRollBackToIt()
	{
		IReadOnlyList<ReleaseGroup> continuation = ThreeReleases().Skip( 1 ).ToList();

		RollbackForecast forecast = RollbackPlanner.Plan(
			continuation,
			Done( "r1" ),
			Deployed(),
			RollbackTarget.ToRelease( "r1" )
		);

		Assert.True( forecast.NothingToRollBack );
	}

	// ---------------------------------------------------------------- OfPlans

	[Fact]
	public void OfPlansTouchesOnlyTheLatestRelease()
	{
		IReadOnlyList<ReleaseGroup> releases = ThreeReleases();

		Assert.Equal(
			[ "r2" ],
			Names( RollbackPlanner.ReleasesToRollBack( releases, Partway( "r2" ), RollbackTarget.OfPlans( [ "c" ] ) ) )
		);

		RollbackForecast forecast = RollbackPlanner.Plan(
			releases,
			Done( "r2" ),
			Deployed( ( "r2", [ "b", "c" ] ) ),
			RollbackTarget.OfPlans( [ "c" ] )
		);

		Assert.Equal( [ "r2/c" ], Undone( forecast ) );
	}

	[Fact]
	public void SeveralPlansAreUndoneInReverseBundleOrder()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			ThreeReleases(),
			Done( "r2" ),
			Deployed( ( "r2", [ "b", "c" ] ) ),
			RollbackTarget.OfPlans( [ "c", "b" ] )
		);

		// listed in deployment order; the engine pops them newest first
		Assert.Equal( [ "r2/b", "r2/c" ], Undone( forecast ) );
	}

	[Fact]
	public void ListedPlansThatAreNotDeployedAreSkipped()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			ThreeReleases(),
			Partway( "r2" ),
			Deployed( ( "r2", [ "b" ] ) ),
			RollbackTarget.OfPlans( [ "b", "c" ] )
		);

		Assert.Equal( [ "r2/b" ], Undone( forecast ) );
	}

	[Fact]
	public void AnUnknownPlanIsRefused()
	{
		RollbackRefusedException error = Assert.Throws<RollbackRefusedException>( () => RollbackPlanner.Plan(
				ThreeReleases(),
				Done( "r2" ),
				Deployed( ( "r2", [ "b", "c" ] ) ),
				RollbackTarget.OfPlans( [ "b", "nope" ] )
			)
		);

		Assert.Contains( "nope", error.Message );
	}

	[Fact]
	public void APlanOfAnEarlierReleaseIsRefused()
	{
		RollbackRefusedException error = Assert.Throws<RollbackRefusedException>( () => RollbackPlanner.Plan(
				ThreeReleases(),
				Done( "r2" ),
				Deployed( ( "r2", [ "b", "c" ] ) ),
				RollbackTarget.OfPlans( [ "a" ] )
			)
		);

		Assert.Contains( "'a'", error.Message );
		Assert.Contains( "belong to a release before", error.Message );
	}

	[Fact]
	public void APlanOfAnEarlierReleaseIsRefusedBesideOnesOfTheLatest()
		=> Assert.Throws<RollbackRefusedException>( () => RollbackPlanner.ReleasesToRollBack(
				ThreeReleases(),
				Done( "r2" ),
				RollbackTarget.OfPlans( [ "a", "b" ] )
			)
		);

	[Fact]
	public void PlansOnlyOfALaterReleaseAreNothingToRollBack()
	{
		IReadOnlyList<ReleaseGroup> releases = ThreeReleases();

		Assert.Empty( RollbackPlanner.ReleasesToRollBack( releases, Done( "r2" ), RollbackTarget.OfPlans( [ "d" ] ) ) );

		RollbackForecast forecast = RollbackPlanner.Plan( releases, Done( "r2" ), Deployed(), RollbackTarget.OfPlans( [ "d" ] ) );

		Assert.True( forecast.NothingToRollBack );
	}

	[Fact]
	public void PlansOfTheLatestAndOfALaterReleaseUndoOnlyTheLatestsDeployedOnes()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			ThreeReleases(),
			Done( "r2" ),
			Deployed( ( "r2", [ "b", "c" ] ) ),
			RollbackTarget.OfPlans( [ "c", "d" ] )
		);

		Assert.Equal( [ "r2/c" ], Undone( forecast ) );
	}

	[Fact]
	public void PlansOfTheLatestAndOfALaterReleaseWithNoneDeployedAreNothingToRollBack()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			ThreeReleases(),
			Partway( "r2" ),
			Deployed( ( "r2", [ "b" ] ) ),
			RollbackTarget.OfPlans( [ "c", "d" ] )
		);

		Assert.True( forecast.NothingToRollBack );
	}

	[Fact]
	public void APlanNoGroupHoldsIsRefusedBeforeTheJournalIsRead()
	{
		RollbackRefusedException error = Assert.Throws<RollbackRefusedException>( () => RollbackPlanner.EnsureTarget(
				ThreeReleases(),
				RollbackTarget.OfPlans( [ "b", "nope", "nada" ] )
			)
		);

		Assert.Contains( "'nope'", error.Message );
		Assert.Contains( "'nada'", error.Message );
		Assert.DoesNotContain( "'b'", error.Message );
	}

	[Fact]
	public void OfPlansOnADatabaseAtTheContinuationBaseIsNothingToRollBack()
	{
		IReadOnlyList<ReleaseGroup> continuation = ThreeReleases().Skip( 1 ).ToList();

		Assert.Empty( RollbackPlanner.ReleasesToRollBack( continuation, Done( "r1" ), RollbackTarget.OfPlans( [ "b" ] ) ) );
		Assert.True( RollbackPlanner.Plan( continuation, Done( "r1" ), Deployed(), RollbackTarget.OfPlans( [ "b" ] ) ).NothingToRollBack );
	}

	private static IReadOnlyList<ReleaseGroup> WithDependencies()
		=> [
			new ReleaseGroup(
				"r1",
				"",
				"",
				[
					new DeployablePlan( "base", "", [ new DeployableStep( "s", "" ) ] ),
					new DeployablePlan( "mid", "", [ new DeployableStep( "s", "" ) ], [ "base" ] ),
					new DeployablePlan( "top", "", [ new DeployableStep( "s", "" ) ], [ "mid" ] )
				]
			)
		];

	[Fact]
	public void ADeployedDependantLeftBehindRefusesTheRollback()
	{
		RollbackRefusedException error = Assert.Throws<RollbackRefusedException>( () => RollbackPlanner.Plan(
				WithDependencies(),
				Done( "r1" ),
				Deployed( ( "r1", [ "base", "mid", "top" ] ) ),
				RollbackTarget.OfPlans( [ "mid" ] )
			)
		);

		Assert.Contains( "'top' requires 'mid'", error.Message );
	}

	[Fact]
	public void ADependantAskedForAlongWithItsDependencyIsNotLeftBehind()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			WithDependencies(),
			Done( "r1" ),
			Deployed( ( "r1", [ "base", "mid", "top" ] ) ),
			RollbackTarget.OfPlans( [ "mid", "top" ] )
		);

		Assert.Equal( [ "r1/mid", "r1/top" ], Undone( forecast ) );
	}

	[Fact]
	public void ADependantThatIsNotDeployedDoesNotRefuseTheRollback()
	{
		RollbackForecast forecast = RollbackPlanner.Plan(
			WithDependencies(),
			Partway( "r1" ),
			Deployed( ( "r1", [ "base", "mid" ] ) ),
			RollbackTarget.OfPlans( [ "mid" ] )
		);

		Assert.Equal( [ "r1/mid" ], Undone( forecast ) );
	}

	// ---------------------------------------------------------------- refusals

	[Fact]
	public void AReleaseOutsideTheLineageIsRefused()
	{
		Assert.Throws<RollbackRefusedException>( () => RollbackPlanner.EnsureTarget( ThreeReleases(), RollbackTarget.ToRelease( "nope" ) ) );

		Assert.Throws<RollbackRefusedException>( () => RollbackPlanner.Plan(
				ThreeReleases(),
				null,
				Deployed(),
				RollbackTarget.ToRelease( "nope" )
			)
		);
	}

	[Fact]
	public void EnsureTargetAcceptsEveryReleaseOfTheLineageAndTheContinuationBase()
	{
		IReadOnlyList<ReleaseGroup> continuation = ThreeReleases().Skip( 1 ).ToList();

		RollbackPlanner.EnsureTarget( continuation, RollbackTarget.ToRelease( "r1" ) );
		RollbackPlanner.EnsureTarget( continuation, RollbackTarget.ToRelease( "r2" ) );
		RollbackPlanner.EnsureTarget( continuation, RollbackTarget.All );
		RollbackPlanner.EnsureTarget( continuation, RollbackTarget.OfPlans( [ "b" ] ) );
	}

	[Fact]
	public void ALatestReleaseTheGroupsLackIsIncompatible()
	{
		foreach( RollbackTarget target in new[] { RollbackTarget.All, RollbackTarget.ToRelease( "r1" ), RollbackTarget.OfPlans( [ "a" ] ) } )
			Assert.Throws<IncompatibleBundleException>( () => RollbackPlanner.Plan(
					ThreeReleases(),
					Done( "elsewhere" ),
					Deployed(),
					target
				)
			);
	}

	[Fact]
	public void ADatabaseAtTheContinuationBaseIsIncompatibleWithRollingBackAll()
	{
		IReadOnlyList<ReleaseGroup> continuation = ThreeReleases().Skip( 1 ).ToList();

		Assert.Throws<IncompatibleBundleException>( () => RollbackPlanner.Plan(
				continuation,
				Done( "r1" ),
				Deployed(),
				RollbackTarget.All
			)
		);
	}

	[Fact]
	public void AJournaledPlanTheGroupsLackIsIncompatible()
	{
		Assert.Throws<IncompatibleBundleException>( () => RollbackPlanner.Plan(
				ThreeReleases(),
				Done( "r3" ),
				Deployed( ( "r3", [ "d" ] ), ( "r2", [ "b", "ghost" ] ) ),
				RollbackTarget.ToRelease( "r1" )
			)
		);
	}

	[Fact]
	public void MissingJournalPlansForAReleaseToUndoIsACallersMistake()
	{
		Assert.Throws<ArgumentException>( () => RollbackPlanner.Plan(
				ThreeReleases(),
				Done( "r3" ),
				Deployed( ( "r3", [ "d" ] ) ),
				RollbackTarget.ToRelease( "r1" )
			)
		);
	}
}
