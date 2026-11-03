using Pug.Sqetch.Deployment;

using static Pug.Sqetch.Tests.Deployment.Lineages;

namespace Pug.Sqetch.Tests.Deployment;

/// <summary>
/// <see cref="DeploymentPlanner"/> is the resume decision on its own — no database, no listener — so
/// these pin each branch and each refusal directly; <see cref="DeploymentEngineTests"/> pins that
/// the engine acts on it unchanged.
/// </summary>
public class DeploymentPlannerTests
{
	private static IReadOnlyList<ReleaseGroup> ThreeReleases()
		=> Lineage(
			[ Release( "r1" ), Release( "r2", "r1" ), Release( "r3", "r2" ) ],
			[ Plan( "a", "r1", "s" ), Plan( "b", "r2", "s" ), Plan( "c", "r2", "s" ), Plan( "d", "r3", "s" ) ]
		);

	private static string[] Deployed( DeploymentForecast forecast )
		=> forecast.Releases
			.SelectMany( release => release.PlansToDeploy.Select( plan => $"{release.Group.Name}/{plan.Name}" ) )
			.ToArray();

	// ---------------------------------------------------------------- the four branches

	[Fact]
	public void AnIncompleteReleaseWithoutItsDeployedPlansIsACallersMistake()
	{
		ArgumentException error = Assert.Throws<ArgumentException>( () =>
			DeploymentPlanner.Plan( ThreeReleases(), new JournaledRelease( "r2", Completed: false ), null ) );

		Assert.Equal( "deployedPlans", error.ParamName );
	}

	[Fact]
	public void NoGroupsAgainstACompletedReleaseAreRefusedAsNotFittingIt()
	{
		Assert.Throws<IncompatibleBundleException>( () =>
			DeploymentPlanner.Plan( [ ], new JournaledRelease( "r1", Completed: true ), null ) );
	}

	[Fact]
	public void NothingJournaledDeploysEveryReleaseWhole()
	{
		DeploymentForecast forecast = DeploymentPlanner.Plan( ThreeReleases(), null, null );

		Assert.Null( forecast.Journaled );
		Assert.Empty( forecast.Notes );
		Assert.False( forecast.NothingToDeploy );
		Assert.Null( forecast.ContinuingFrom );
		Assert.Equal( [ "r1/a", "r2/b", "r2/c", "r3/d" ], Deployed( forecast ) );
		Assert.All( forecast.Releases, release => Assert.False( release.Resumed ) );
		Assert.All( forecast.Releases, release => Assert.Empty( release.SkippedPlans ) );
	}

	[Fact]
	public void AnIncompleteReleaseResumesSkippingTheDeployedPlansThenContinues()
	{
		JournaledRelease latest = new( "r2", Completed: false );

		DeploymentForecast forecast = DeploymentPlanner.Plan( ThreeReleases(), latest, [ "b" ] );

		Assert.Equal( latest, forecast.Journaled );
		Assert.Equal( [ "r2/c", "r3/d" ], Deployed( forecast ) );
		Assert.Equal( [ "r2", "r3" ], forecast.Releases.Select( release => release.Group.Name ) );
		Assert.True( forecast.Releases[0].Resumed );
		Assert.Equal( [ "b" ], forecast.Releases[0].SkippedPlans );
		Assert.False( forecast.Releases[1].Resumed );

		// release r1 is behind the resume point
		Assert.Equal( [ new ForecastNote( ForecastNoteKind.SkippingRelease, "r1" ) ], forecast.Notes );
	}

	[Fact]
	public void AnIncompleteReleaseWithEveryPlanDeployedStillResumesAsAnEmptyRelease()
	{
		DeploymentForecast forecast = DeploymentPlanner.Plan( ThreeReleases(), new JournaledRelease( "r2", false ), [ "b", "c" ] );

		Assert.False( forecast.NothingToDeploy );
		Assert.True( forecast.Releases[0].Resumed );
		Assert.Empty( forecast.Releases[0].PlansToDeploy );
		Assert.Equal( [ "r3/d" ], Deployed( forecast ) );
	}

	[Fact]
	public void ACompletedReleaseHandsOverToTheOneDependingOnItWhenTheBundleContainsIt()
	{
		DeploymentForecast forecast = DeploymentPlanner.Plan( ThreeReleases(), new JournaledRelease( "r1", true ), null );

		Assert.Equal( [ "r2/b", "r2/c", "r3/d" ], Deployed( forecast ) );
		Assert.Equal( [ new ForecastNote( ForecastNoteKind.SkippingRelease, "r1" ) ], forecast.Notes );
		Assert.Null( forecast.ContinuingFrom );
	}

	[Fact]
	public void ACompletedReleaseTheBundleDoesNotContainMakesItAContinuation()
	{
		IReadOnlyList<ReleaseGroup> continuation = ThreeReleases().Skip( 1 ).ToList();

		DeploymentForecast forecast = DeploymentPlanner.Plan( continuation, new JournaledRelease( "r1", true ), null );

		Assert.Equal( "r1", forecast.ContinuingFrom );
		Assert.Equal( [ new ForecastNote( ForecastNoteKind.ContinuingFrom, "r1" ) ], forecast.Notes );
		Assert.Equal( [ "r2/b", "r2/c", "r3/d" ], Deployed( forecast ) );
	}

	[Fact]
	public void ADatabaseAtTheBundlesLastReleaseHasNothingToDeploy()
	{
		DeploymentForecast forecast = DeploymentPlanner.Plan( ThreeReleases(), new JournaledRelease( "r3", true ), null );

		Assert.True( forecast.NothingToDeploy );
		Assert.Empty( forecast.Releases );
		Assert.Empty( Deployed( forecast ) );
		Assert.Equal(
			[
				new ForecastNote( ForecastNoteKind.SkippingRelease, "r1" ),
				new ForecastNote( ForecastNoteKind.SkippingRelease, "r2" ),
				new ForecastNote( ForecastNoteKind.SkippingRelease, "r3" )
			],
			forecast.Notes );
	}

	[Fact]
	public void NothingToDeployForAnEmptyBundleOnAFreshDatabase()
		=> Assert.True( DeploymentPlanner.Plan( [ ], null, null ).NothingToDeploy );

	// ---------------------------------------------------------------- the pseudo-release

	[Fact]
	public void TheUnreleasedPlansPseudoReleaseIsAnOrdinaryLinkAfterTheLastRealRelease()
	{
		IReadOnlyList<ReleaseGroup> groups = Lineage( [ Release( "r1" ) ], [ Plan( "a", "r1", "s" ), Plan( "u", "", "s" ) ] );

		DeploymentForecast forecast = DeploymentPlanner.Plan( groups, new JournaledRelease( "r1", true ), null );

		Assert.Equal( [ "/u" ], Deployed( forecast ) );
	}

	[Fact]
	public void NothingMayFollowACompletedPseudoRelease()
	{
		IReadOnlyList<ReleaseGroup> groups = Lineage( [ Release( "r1" ) ], [ Plan( "a", "r1", "s" ), Plan( "u", "", "s" ) ] );

		IncompatibleBundleException error = Assert.Throws<IncompatibleBundleException>( () =>
			DeploymentPlanner.Plan( groups, new JournaledRelease( "", true ), null ) );

		Assert.Contains( "pseudo bundle", error.Message );
	}

	// ---------------------------------------------------------------- refusals

	[Fact]
	public void AContinuationBundleIsRefusedOnAFreshDatabase()
	{
		IReadOnlyList<ReleaseGroup> continuation = ThreeReleases().Skip( 1 ).ToList();

		IncompatibleBundleException error =
			Assert.Throws<IncompatibleBundleException>( () => DeploymentPlanner.Plan( continuation, null, null ) );

		Assert.Null( error.Journaled );
		Assert.Contains( "continues from release 'r1'", error.Message );
		Assert.Contains( "no deployed releases", error.Message );
	}

	[Fact]
	public void APartlyDeployedReleaseTheBundleLacksIsNotResumable()
	{
		IReadOnlyList<ReleaseGroup> continuation = ThreeReleases().Skip( 2 ).ToList();
		JournaledRelease latest = new( "r2", Completed: false );

		IncompatibleBundleException error =
			Assert.Throws<IncompatibleBundleException>( () => DeploymentPlanner.EnsureResumable( latest, continuation ) );

		Assert.Equal( latest, error.Journaled );
		Assert.Contains( "release 'r2' is only partially deployed", error.Message );
		Assert.Contains( "before release 'r3'", error.Message );
	}

	[Fact]
	public void EnsureResumableAcceptsACompleteReleaseOrOneTheBundleContains()
	{
		DeploymentPlanner.EnsureResumable( new JournaledRelease( "r9", true ), ThreeReleases() );
		DeploymentPlanner.EnsureResumable( new JournaledRelease( "r2", false ), ThreeReleases() );
	}

	[Fact]
	public void AJournaledPlanTheBundlesReleaseLacksIsRefusedNamingIt()
	{
		JournaledRelease latest = new( "r2", Completed: false );

		IncompatibleBundleException error = Assert.Throws<IncompatibleBundleException>( () =>
			DeploymentPlanner.Plan( ThreeReleases(), latest, [ "b", "gone" ] ) );

		Assert.Equal( latest, error.Journaled );
		Assert.Contains( "'gone'", error.Message );
		Assert.DoesNotContain( "'b'", error.Message );
	}

	[Fact]
	public void ADatabaseAtAReleaseTheBundleNeitherContainsNorContinuesIsRefused()
	{
		JournaledRelease latest = new( "elsewhere", Completed: true );

		IncompatibleBundleException error =
			Assert.Throws<IncompatibleBundleException>( () => DeploymentPlanner.Plan( ThreeReleases(), latest, null ) );

		Assert.Equal( latest, error.Journaled );
		Assert.Contains( "release 'elsewhere', which this bundle neither contains nor continues from", error.Message );
	}

	[Fact]
	public void ADatabaseAheadOfTheBundleIsRefused()
	{
		IReadOnlyList<ReleaseGroup> older = ThreeReleases().Take( 1 ).ToList();

		Assert.Throws<IncompatibleBundleException>( () =>
			DeploymentPlanner.Plan( older, new JournaledRelease( "r3", true ), null ) );
	}

	// ---------------------------------------------------------------- lineage

	[Fact]
	public void EnsureLineageAcceptsAContiguousChainStartingAnywhere()
	{
		DeploymentPlanner.EnsureLineage( ThreeReleases() );
		DeploymentPlanner.EnsureLineage( ThreeReleases().Skip( 1 ).ToList() );
		DeploymentPlanner.EnsureLineage( [ ] );
	}

	[Fact]
	public void EnsureLineageRefusesARepeatedNameAndABrokenLink()
	{
		IReadOnlyList<ReleaseGroup> groups = ThreeReleases();

		Assert.Contains( "more than once", Assert.Throws<ArgumentException>( () =>
			DeploymentPlanner.EnsureLineage( [ groups[0], groups[1], groups[1] ] ) ).Message );

		Assert.Contains( "does not depend on 'r1'", Assert.Throws<ArgumentException>( () =>
			DeploymentPlanner.EnsureLineage( [ groups[0], groups[2] ] ) ).Message );
	}

	// ---------------------------------------------------------------- notes

	[Fact]
	public void NotesAreInTheOrderTheWalkMeetsTheReleases()
	{
		IReadOnlyList<ReleaseGroup> groups = Lineage(
			[ Release( "r1" ), Release( "r2", "r1" ), Release( "r3", "r2" ), Release( "r4", "r3" ) ],
			[ Plan( "a", "r1", "s" ), Plan( "b", "r2", "s" ), Plan( "c", "r3", "s" ), Plan( "d", "r4", "s" ) ] );

		DeploymentForecast forecast = DeploymentPlanner.Plan( groups, new JournaledRelease( "r3", false ), [ ] );

		Assert.Equal(
			[
				new ForecastNote( ForecastNoteKind.SkippingRelease, "r1" ),
				new ForecastNote( ForecastNoteKind.SkippingRelease, "r2" )
			],
			forecast.Notes );

		Assert.Equal( [ "r3/c", "r4/d" ], Deployed( forecast ) );
	}

	[Fact]
	public void AContinuationNoteComesAfterTheSkipsAndOnlyOnce()
	{
		IReadOnlyList<ReleaseGroup> groups = Lineage(
			[ Release( "r2", "r1" ), Release( "r3", "r2" ) ],
			[ Plan( "b", "r2", "s" ), Plan( "c", "r3", "s" ) ] );

		DeploymentForecast forecast = DeploymentPlanner.Plan( groups, new JournaledRelease( "r1", true ), null );

		Assert.Equal( [ new ForecastNote( ForecastNoteKind.ContinuingFrom, "r1" ) ], forecast.Notes );
		Assert.Equal( [ "r2", "r3" ], forecast.Releases.Select( release => release.Group.Name ) );
	}
}
