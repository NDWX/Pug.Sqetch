using Pug.Sqetch.Planning;
using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

namespace Pug.Sqetch.Tests.Planning.Stores.FileSystem;

/// <summary>
/// Criteria-based listing: filtering happens in the store, and released plans come back
/// grouped by release with the groups in release-chronological (dependency-chain) order.
/// Ordering of individual plans is the business layer's job and is not asserted here.
/// </summary>
public class ListingTests
{
	private static readonly UserInfo Alice = new( "alice", "alice@example.com" );
	private static readonly UserInfo Bob = new( "bob", "bob@example.com" );

	private static ActionContext At( UserInfo user, string timestamp )
		=> new( user, DateTime.Parse( timestamp ) );

	[Theory]
	[MemberData( nameof(ShardingCases.Names), MemberType = typeof(ShardingCases) )]
	public void ReleasesAreFilteredByStateWindowsAndUsers( string sharding )
	{
		using TempProject project = TempProject.Create( ShardingCases.Configuration( sharding ) );
		using FileSystemProjectStores stores = project.Open();

		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.01", "", "" ), At( Alice, "2026-01-05" ) );
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.02", "", "2026.01" ), At( Bob, "2026-02-05" ) );
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.03", "", "2026.02" ), At( Alice, "2026-03-05" ) );

		stores.InfoStore.SetReleaseContext( "2026.01", At( Alice, "2026-01-20" ) );
		stores.InfoStore.SetReleaseContext( "2026.02", At( Bob, "2026-02-20" ) );

		// default criteria: unfinalized releases only
		Assert.Equal( [ "2026.03" ], Names( stores.InfoStore.ListReleases( new ReleaseSearchCriteria() ) ) );

		Assert.Equal(
			[ "2026.01", "2026.02" ],
			Names( stores.InfoStore.ListReleases( new ReleaseSearchCriteria( Finalized: true ) ) ).Order().ToArray()
		);

		// a finalize window or finalize user implies finalized without the explicit flag
		Assert.Equal(
			[ "2026.01" ],
			Names(
				stores.InfoStore.ListReleases(
					new ReleaseSearchCriteria(
						FinalizeTimestamp: new Range<DateTime>
						{
							Start = DateTime.Parse( "2026-01-01" ), End = DateTime.Parse( "2026-01-31" )
						}
					)
				)
			)
		);

		Assert.Equal(
			[ "2026.02" ],
			Names( stores.InfoStore.ListReleases( new ReleaseSearchCriteria( FinalizeUser: "BOB@example.com" ) ) )
		);

		Assert.Equal(
			[ "2026.01" ],
			Names(
				stores.InfoStore.ListReleases(
					new ReleaseSearchCriteria(
						Finalized: true,
						CreateUser: "alice@example.com"
					)
				)
			)
		);

		Assert.Equal(
			[ "2026.03" ],
			Names(
				stores.InfoStore.ListReleases(
					new ReleaseSearchCriteria(
						CreateTimestamp: new Range<DateTime>
						{
							Start = DateTime.Parse( "2026-03-01" ), End = DateTime.Parse( "2026-03-31" )
						}
					)
				)
			)
		);

		Assert.Empty( stores.InfoStore.ListReleases( new ReleaseSearchCriteria( Prefix: "2025" ) ) );
	}

	[Theory]
	[MemberData( nameof(ShardingCases.Names), MemberType = typeof(ShardingCases) )]
	public void DefaultPlanCriteriaSelectsUnreleasedPlansOnly( string sharding )
	{
		using TempProject project = TempProject.Create( ShardingCases.Configuration( sharding ) );
		using FileSystemProjectStores stores = project.Open();

		stores.InfoStore.AddPlan( new ObjectDefinition( "by-alice", "" ), [ ], At( Alice, "2026-01-01" ) );
		stores.InfoStore.AddPlan( new ObjectDefinition( "by-bob", "" ), [ ], At( Bob, "2026-01-02" ) );
		stores.InfoStore.AddPlan( new ObjectDefinition( "shipped", "" ), [ ], At( Alice, "2026-01-03" ) );

		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.01", "", "" ), At( Alice, "2026-01-05" ) );
		stores.InfoStore.AddReleasePlan( "2026.01", "shipped", At( Alice, "2026-01-06" ) );

		Assert.Equal(
			[ "by-alice", "by-bob" ],
			Names( stores.InfoStore.ListPlans( new PlanSearchCriteria() ) ).Order().ToArray()
		);

		// create-user filter, case-insensitive
		Assert.Equal(
			[ "by-alice" ],
			Names( stores.InfoStore.ListPlans( new PlanSearchCriteria( CreateUser: "ALICE@example.com" ) ) )
		);
	}

	[Theory]
	[MemberData( nameof(ShardingCases.Names), MemberType = typeof(ShardingCases) )]
	public void ReleasedPlansAreGroupedByReleaseChronology( string sharding )
	{
		using TempProject project = TempProject.Create( ShardingCases.Configuration( sharding ) );
		using FileSystemProjectStores stores = project.Open();

		// the dependant release is registered with the EARLIER timestamp, so only the
		// dependency chain can put '2026.01' before '2026.02'
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.02", "", "2026.01" ), At( Alice, "2026-01-01" ) );
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.01", "", "" ), At( Alice, "2026-06-01" ) );
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2025.12", "", "" ), At( Alice, "2025-12-01" ) );

		foreach( ( string plan, string release, UserInfo user ) in new[]
				{
					( "second-a", "2026.02", Alice ), ( "second-b", "2026.02", Bob ),
					( "first-a", "2026.01", Alice ), ( "december", "2025.12", Bob )
				} )
		{
			stores.InfoStore.AddPlan( new ObjectDefinition( plan, "" ), [ ], At( user, "2026-06-02" ) );
			stores.InfoStore.AddReleasePlan( release, plan, At( user, "2026-06-02" ) );
		}

		ProjectPlan[] released = stores.InfoStore.ListPlans( new PlanSearchCriteria( Released: true ) ).ToArray();

		// groups in chain order: 2025.12 (unchained, earliest), then 2026.01, then its dependant
		Assert.Equal(
			[ "2025.12", "2026.01", "2026.02", "2026.02" ],
			released.Select( x => x.Release ).ToArray()
		);

		Assert.Equal(
			[ "december", "first-a", "second-a", "second-b" ],
			released.Select( x => x.Definition.Name ).OrderBy( x => x ).ToArray()
		);

		// user filter applies within the release walk
		Assert.Equal(
			[ "december", "second-b" ],
			Names(
					stores.InfoStore.ListPlans(
						new PlanSearchCriteria(
							Released: true,
							CreateUser: "bob@example.com"
						)
					)
				)
				.Order()
				.ToArray()
		);
	}

	[Theory]
	[MemberData( nameof(ShardingCases.Names), MemberType = typeof(ShardingCases) )]
	public void PlansAreFilteredByReleaseAndFinalizeWindow( string sharding )
	{
		using TempProject project = TempProject.Create( ShardingCases.Configuration( sharding ) );
		using FileSystemProjectStores stores = project.Open();

		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.01", "", "" ), At( Alice, "2026-01-01" ) );
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.02", "", "2026.01" ), At( Alice, "2026-02-01" ) );

		foreach( ( string plan, string release ) in new[] { ( "early", "2026.01" ), ( "late", "2026.02" ) } )
		{
			stores.InfoStore.AddPlan( new ObjectDefinition( plan, "" ), [ ], At( Alice, "2026-01-02" ) );
			stores.InfoStore.AddReleasePlan( release, plan, At( Alice, "2026-01-02" ) );
		}

		stores.InfoStore.SetReleaseContext( "2026.01", At( Alice, "2026-01-15" ) );

		Assert.Equal(
			[ "late" ],
			Names( stores.InfoStore.ListPlans( new PlanSearchCriteria( Release: "2026.02" ) ) )
		);

		// a finalize window implies finalized releases only, so 'late' (open release) drops out
		Assert.Equal(
			[ "early" ],
			Names(
				stores.InfoStore.ListPlans(
					new PlanSearchCriteria(
						ReleaseFinalizeTimestamp: new Range<DateTime>
						{
							Start = DateTime.Parse( "2026-01-01" ), End = DateTime.Parse( "2026-01-31" )
						}
					)
				)
			)
		);

		Assert.Empty(
			stores.InfoStore.ListPlans(
				new PlanSearchCriteria(
					ReleaseFinalizeTimestamp: new Range<DateTime>
					{
						Start = DateTime.Parse( "2026-02-01" ), End = DateTime.Parse( "2026-02-28" )
					}
				)
			)
		);

		Assert.Throws<UnknownReleaseException>( () =>
			stores.InfoStore.ListPlans( new PlanSearchCriteria( Release: "2027.01" ) )
		);
	}

	private static string[] Names( IEnumerable<ProjectRelease> releases )
		=> releases.Select( x => x.Definition.Name ).ToArray();

	private static string[] Names( IEnumerable<ProjectPlan> plans )
		=> plans.Select( x => x.Definition.Name ).ToArray();
}