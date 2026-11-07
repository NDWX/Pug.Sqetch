using System.Security.Cryptography;

using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

namespace Pug.Sqetch.Tests.Planning.Stores.FileSystem;

public class ProjectCheckStoreTests
{
	[Theory]
	[MemberData( nameof(ShardingCases.Names), MemberType = typeof(ShardingCases) )]
	public void ACleanProjectHasNoFindings( string sharding )
	{
		using TempProject project = Build( ShardingCases.Configuration( sharding ) );

		Assert.Empty( Check( project ) );
	}

	[Fact]
	public void AMissingIndexIsAFindingAndTheFolderChecksStillRun()
	{
		using TempProject project = Build();

		File.Delete( IndexFile( project ) );
		Rewrite( StepFile( project, "2026.07", "base", "seed" ), "\"name\": \"seed\"", "\"name\": \"sown\"" );

		IReadOnlyList<ProjectFinding> findings = Check( project );

		Assert.Equal( [ ProjectChecks.IndexMissing, ProjectChecks.StepNameMismatch ], findings.Select( x => x.Check ).Order() );

		ProjectFinding missing = Assert.Single( findings, x => x.Check == ProjectChecks.IndexMissing );

		Assert.Equal( ( "", "", "", true ), ( missing.Release, missing.Plan, missing.Step, missing.FixedByReindex ) );
	}

	[Fact]
	public void AnUnreadableIndexIsAFinding()
	{
		using TempProject project = Build();

		File.WriteAllText( IndexFile( project ), "mangled by a bad merge" );

		ProjectFinding finding = Assert.Single( Check( project ) );

		Assert.Equal( ProjectChecks.IndexUnreadable, finding.Check );
		Assert.True( finding.FixedByReindex );
	}

	[Fact]
	public void AnIndexEntryWithNoPlanFolderIsDrift()
	{
		using TempProject project = Build();

		File.AppendAllText( IndexFile( project ), "ghost\t2026.07\t\n" );

		ProjectFinding finding = Assert.Single( Check( project ) );

		Assert.Equal( ( ProjectChecks.IndexDrift, "2026.07", "ghost", "", true ), Fields( finding ) );
		Assert.Contains( "no plan folder", finding.Message );
	}

	[Fact]
	public void APlanFolderWithNoIndexEntryIsDrift()
	{
		using TempProject project = Build();

		string[] lines = File.ReadAllLines( IndexFile( project ) ).Where( x => !x.StartsWith( "loose\t" ) ).ToArray();

		File.WriteAllLines( IndexFile( project ), lines );

		ProjectFinding finding = Assert.Single( Check( project ) );

		Assert.Equal( ( ProjectChecks.IndexDrift, "", "loose", "", true ), Fields( finding ) );
		Assert.Contains( "no entry", finding.Message );
	}

	[Fact]
	public void AnIndexEntryWithAnotherReleaseOrDependenciesIsDrift()
	{
		using TempProject project = Build();

		Rewrite( IndexFile( project ), "customer-email\t2026.07\tbase", "customer-email\t2026.08\t" );

		IReadOnlyList<ProjectFinding> findings = Check( project );

		Assert.Equal( 2, findings.Count );
		Assert.All( findings, x => Assert.Equal( ( ProjectChecks.IndexDrift, "2026.07", "customer-email", "", true ), Fields( x ) ) );
		Assert.Contains( findings, x => x.Message.Contains( "2026.08" ) );
		Assert.Contains( findings, x => x.Message.Contains( "dependencies" ) );
	}

	[Fact]
	public void APlanJsonReleaseLeftBehindByAMovedFolderIsAMismatch()
	{
		using TempProject project = Build();

		Directory.Move(
			Path.Combine( project.Root, "releases", "2026.08", "plans", "extra" ),
			Path.Combine( project.Root, "plans", "extra" )
		);

		IReadOnlyList<ProjectFinding> findings = Check( project );

		Assert.Contains( ( ProjectChecks.PlanReleaseMismatch, "", "extra", "", true ), findings.Select( Fields ) );
		Assert.Contains( ( ProjectChecks.IndexDrift, "", "extra", "", true ), findings.Select( Fields ) );
	}

	[Fact]
	public void APlanJsonNamingAnotherPlanThanItsFolderIsNotFixedByReindex()
	{
		using TempProject project = Build();

		Rewrite( PlanFile( project, "", "loose" ), "\"name\": \"loose\"", "\"name\": \"tight\"" );

		IReadOnlyList<ProjectFinding> findings = Check( project );

		ProjectFinding mismatch = Assert.Single( findings, x => x.Check == ProjectChecks.PlanNameMismatch );

		Assert.Equal( ( ProjectChecks.PlanNameMismatch, "", "loose", "", false ), Fields( mismatch ) );
	}

	[Fact]
	public void AStepJsonNamingAnotherStepThanItsFolderIsAMismatch()
	{
		using TempProject project = Build();

		Rewrite( StepFile( project, "2026.07", "base", "seed" ), "\"name\": \"seed\"", "\"name\": \"sown\"" );

		ProjectFinding finding = Assert.Single( Check( project ) );

		Assert.Equal( ( ProjectChecks.StepNameMismatch, "2026.07", "base", "seed", true ), Fields( finding ) );
	}

	[Fact]
	public void AStepRequiringNoStepOfItsPlanIsFlaggedCaseInsensitively()
	{
		using TempProject project = Build();

		Rewrite( StepFile( project, "2026.07", "base", "seed" ), "\"create\"", "\"CREATE\", \"old-name\"" );

		ProjectFinding finding = Assert.Single( Check( project ) );

		Assert.Equal( ( ProjectChecks.StepDependencyUnknown, "2026.07", "base", "seed", true ), Fields( finding ) );
		Assert.Contains( "old-name", finding.Message );
	}

	[Fact]
	public void InvalidNamesAreFoundForTheProjectReleasesPlansAndSteps()
	{
		using TempProject project = Build();

		Rewrite( Path.Combine( project.Root, "sqetch-project.json" ), "\"name\": \"test-project\"", "\"name\": \".bad\"" );
		Move( StepDirectory( project, "2026.08", "extra", "go" ), "bad step" );
		Move( PlanDirectory( project, "", "loose" ), "-loose" );
		Move( Path.Combine( project.Root, "releases", "2026.08" ), "bad release" );

		ProjectFinding[] invalid = Check( project ).Where( x => x.Check == ProjectChecks.InvalidName ).ToArray();

		Assert.Equal( 4, invalid.Length );
		Assert.All( invalid, x => Assert.False( x.FixedByReindex ) );

		Assert.Equal(
			[
				( "", "", "" ),
				( "", "-loose", "" ),
				( "bad release", "", "" ),
				( "bad release", "extra", "bad step" )
			],
			invalid.Select( x => ( x.Release, x.Plan, x.Step ) ).ToArray()
		);
	}

	[Fact]
	public void AnIndexEntryDifferingFromItsPlanOnlyInCaseIsDriftFixedByReindex()
	{
		using TempProject project = Build();

		Rewrite( IndexFile( project ), "loose\t", "LOOSE\t" );

		ProjectFinding finding = Assert.Single( Check( project ) );

		Assert.Equal( ( ProjectChecks.IndexDrift, "", "LOOSE", "", true ), Fields( finding ) );
		Assert.Contains( "'LOOSE'", finding.Message );

		using( FileSystemProjectStores stores = project.Open() )
			stores.Reindex();

		Assert.Empty( Check( project ) );
	}

	[Fact]
	public void APlanInTwoFoldersIsADuplicateNotFixedByReindex()
	{
		using TempProject project = Build();

		CopyDirectory( PlanDirectory( project, "", "loose" ), PlanDirectory( project, "2026.08", "loose-copy" ) );
		Rewrite( PlanFile( project, "2026.08", "loose-copy" ), "\"name\": \"loose\"", "\"name\": \"LOOSE\"" );

		IReadOnlyList<ProjectFinding> findings = Check( project );

		ProjectFinding duplicate = Assert.Single( findings, x => x.Check == ProjectChecks.PlanDuplicate );

		Assert.Equal( ( ProjectChecks.PlanDuplicate, "2026.08", "loose-copy", "", false ), Fields( duplicate ) );
		Assert.Contains( "plans/loose", duplicate.Message );
	}

	[Fact]
	public void TwoReleaseFoldersNamingTheSameReleaseAreADuplicate()
	{
		using TempProject project = Build();

		string copy = Path.Combine( project.Root, "releases", "2026.08-copy" );

		CopyDirectory( Path.Combine( project.Root, "releases", "2026.08" ), copy );
		Directory.Delete( Path.Combine( copy, "plans" ), true );

		IReadOnlyList<ProjectFinding> findings = Check( project );

		ProjectFinding duplicate = Assert.Single( findings, x => x.Check == ProjectChecks.ReleaseDuplicate );

		Assert.Equal( ( ProjectChecks.ReleaseDuplicate, "2026.08-copy", "", "", false ), Fields( duplicate ) );
		Assert.Contains( ( ProjectChecks.ReleaseNameMismatch, "2026.08-copy", "", "", false ), findings.Select( Fields ) );
	}

	[Fact]
	public void ReleasesDifferingOnlyInCaseAreADuplicate()
	{
		using TempProject project = Build();

		foreach( string name in new[] { "Alpha", "alpha" } )
		{
			string directory = Path.Combine( project.Root, "releases", name );

			CopyDirectory( Path.Combine( project.Root, "releases", "2026.08" ), directory );
			Directory.Delete( Path.Combine( directory, "plans" ), true );
			Rewrite( Path.Combine( directory, "release.json" ), "\"name\": \"2026.08\"", $"\"name\": \"{name}\"" );
		}

		ProjectFinding duplicate = Assert.Single( Check( project ), x => x.Check == ProjectChecks.ReleaseDuplicate );

		Assert.Equal( ( ProjectChecks.ReleaseDuplicate, "alpha", "", "", false ), Fields( duplicate ) );
	}

	[Fact]
	public void AReleaseJsonNamingAnotherReleaseThanItsFolderIsAMismatch()
	{
		using TempProject project = Build();

		string file = Path.Combine( project.Root, "releases", "2026.08", "release.json" );

		Rewrite( file, "\"name\": \"2026.08\"", "\"name\": \"2026.09\"" );

		ProjectFinding finding = Assert.Single( Check( project ) );

		Assert.Equal( ( ProjectChecks.ReleaseNameMismatch, "2026.08", "", "", false ), Fields( finding ) );
	}

	[Fact]
	public void UnreadableDocumentsAreFindingsAndTheOtherDocumentsAreStillChecked()
	{
		using TempProject project = Build();

		File.WriteAllText( PlanFile( project, "", "loose" ), "<<<<<<< HEAD\n{}\n=======\n" );
		File.WriteAllText( StepFile( project, "2026.07", "base", "seed" ), "<<<<<<< HEAD" );
		File.WriteAllText( ReleaseFile( project, "2026.08" ), "<<<<<<< HEAD" );
		Rewrite( StepFile( project, "2026.08", "extra", "go" ), "\"name\": \"go\"", "\"name\": \"went\"" );

		IReadOnlyList<ProjectFinding> findings = Check( project );

		Assert.Equal(
			[
				( ProjectChecks.DocumentUnreadable, "", "loose", "", false ),
				( ProjectChecks.DocumentUnreadable, "2026.07", "base", "seed", false ),
				( ProjectChecks.DocumentUnreadable, "2026.08", "", "", false )
			],
			findings.Where( x => x.Check == ProjectChecks.DocumentUnreadable ).Select( Fields ).ToArray()
		);

		Assert.Contains( "plans/loose/plan.json", findings.First( x => x.Check == ProjectChecks.DocumentUnreadable ).Message );
		Assert.Contains( ( ProjectChecks.StepNameMismatch, "2026.08", "extra", "go", true ), findings.Select( Fields ) );
	}

	/// <summary>
	/// Valid JSON lacking a member every read relies on would surface as a null reference in the
	/// engine's check; the store's reports it as unreadable instead, which also blocks the engine's.
	/// </summary>
	[Fact]
	public void DocumentsLackingRequiredMembersAreUnreadable()
	{
		using TempProject project = Build();

		File.WriteAllText( PlanFile( project, "", "loose" ), "{ \"name\": \"loose\", \"dependencies\": [] }" );
		File.WriteAllText( StepFile( project, "2026.07", "base", "seed" ), "{ \"name\": \"seed\" }" );

		IReadOnlyList<ProjectFinding> findings = Check( project );

		ProjectFinding plan = findings.Single( x => x.Check == ProjectChecks.DocumentUnreadable && x.Plan == "loose" );
		ProjectFinding step = findings.Single( x => x.Check == ProjectChecks.DocumentUnreadable && x.Step == "seed" );

		Assert.Contains( "'registration'", plan.Message );
		Assert.Contains( "'dependencies'", step.Message );
		Assert.Contains( "'registration'", step.Message );
		Assert.True( ProjectChecks.IsBlocking( plan.Check ) );
	}

	[Fact]
	public void CheckWritesNothingOnAProjectWithAnUnreadableDocument()
	{
		using TempProject project = Build();

		File.WriteAllText( PlanFile( project, "", "loose" ), "<<<<<<< HEAD" );
		File.WriteAllText( ReleaseFile( project, "2026.08" ), "<<<<<<< HEAD" );

		Dictionary<string, string> before = Hashes( project.Root );

		Assert.NotEmpty( Check( project ) );
		Assert.Equal( before, Hashes( project.Root ) );
	}

	[Fact]
	public void APlanFolderWithoutPlanJsonIsAnOrphan()
	{
		using TempProject project = Build();

		Directory.CreateDirectory( Path.Combine( project.Root, "plans", "stray", "steps" ) );
		Directory.CreateDirectory( Path.Combine( project.Root, "releases", "2026.08", "plans", "stray2" ) );

		IReadOnlyList<ProjectFinding> orphans = Check( project ).Where( x => x.Check == ProjectChecks.OrphanFolder ).ToArray();

		Assert.Equal(
			[
				( ProjectChecks.OrphanFolder, "", "stray", "", false ),
				( ProjectChecks.OrphanFolder, "2026.08", "stray2", "", false )
			],
			orphans.Select( Fields ).ToArray()
		);
	}

	[Theory]
	[MemberData( nameof(ShardingCases.Names), MemberType = typeof(ShardingCases) )]
	public void AReleaseFolderWithoutReleaseJsonIsAnOrphan( string sharding )
	{
		using TempProject project = Build( ShardingCases.Configuration( sharding ) );

		string folder = project.Open().Paths.FinalizedReleaseDirectory( "2026.09" );

		Directory.CreateDirectory( Path.Combine( folder, "plans" ) );

		ProjectFinding finding = Assert.Single( Check( project ) );

		Assert.Equal( ( ProjectChecks.OrphanFolder, "2026.09", "", "", false ), Fields( finding ) );
		Assert.Contains( "releases/", finding.Message );
	}

	[Fact]
	public void ABrokenProjectReportsEveryIndependentFinding()
	{
		using TempProject project = Build();

		Break( project );

		File.WriteAllText( StepFile( project, "2026.07", "base", "create" ), "<<<<<<<" );
		Directory.CreateDirectory( Path.Combine( project.Root, "plans", "stray" ) );
		Rewrite( Path.Combine( project.Root, "releases", "2026.08", "release.json" ), "\"name\": \"2026.08\"", "\"name\": \"x\"" );
		CopyDirectory( PlanDirectory( project, "", "loose" ), PlanDirectory( project, "2026.08", "tight2" ) );

		string[] checks = Check( project ).Select( x => x.Check ).Distinct().Order().ToArray();

		Assert.Equal(
			[
				ProjectChecks.DocumentUnreadable,
				ProjectChecks.IndexUnreadable,
				ProjectChecks.InvalidName,
				ProjectChecks.OrphanFolder,
				ProjectChecks.PlanDuplicate,
				ProjectChecks.PlanNameMismatch,
				ProjectChecks.PlanReleaseMismatch,
				ProjectChecks.ReleaseNameMismatch,
				ProjectChecks.StepDependencyUnknown,
				ProjectChecks.StepNameMismatch
			],
			checks
		);
	}

	[Fact]
	public void ReindexRepairsExactlyTheFindingsMarkedFixedByReindex()
	{
		using TempProject project = Build();

		Break( project );

		IReadOnlyList<ProjectFinding> before = Check( project );

		Assert.Contains( before, x => x.FixedByReindex );
		Assert.Contains( before, x => !x.FixedByReindex );

		using( FileSystemProjectStores stores = project.Open() )
			stores.Reindex();

		IReadOnlyList<ProjectFinding> after = Check( project );

		Assert.Equal( before.Where( x => !x.FixedByReindex ).ToArray(), after.ToArray() );
		Assert.Equal( [ ProjectChecks.InvalidName, ProjectChecks.PlanNameMismatch ], after.Select( x => x.Check ).Order() );
	}

	[Fact]
	public void CheckWritesNothingEvenOnABrokenProject()
	{
		using TempProject project = Build();

		Break( project );

		Dictionary<string, string> before = Hashes( project.Root );

		Assert.NotEmpty( Check( project ) );
		Assert.Equal( before, Hashes( project.Root ) );

		File.Delete( IndexFile( project ) );
		before = Hashes( project.Root );

		Assert.NotEmpty( Check( project ) );
		Assert.Equal( before, Hashes( project.Root ) );
	}

	[Fact]
	public void FindingsComeInAStableOrder()
	{
		using TempProject project = Build();

		Break( project );

		ProjectFinding[] first = Check( project ).ToArray();

		Assert.Equal( first, Check( project ).ToArray() );
		Assert.Equal( first.Select( x => x.Release ).Order( StringComparer.Ordinal ), first.Select( x => x.Release ) );
	}

	/// <summary>
	/// A project with an unreleased plan, a finalized (so sharded) release of two plans and an
	/// unfinalized release of one, each plan with steps.
	/// </summary>
	private static TempProject Build( ShardingConfiguration? sharding = null )
	{
		TempProject project = TempProject.Create( sharding );

		using FileSystemProjectStores stores = project.Open();

		stores.InfoStore.AddPlan( new ObjectDefinition( "loose", "" ), [ ], TestData.Context() );
		stores.InfoStore.AddPlan( new ObjectDefinition( "base", "" ), [ ], TestData.Context() );
		stores.InfoStore.AddPlan( new ObjectDefinition( "customer-email", "" ), [ "base" ], TestData.Context() );
		stores.InfoStore.AddPlan( new ObjectDefinition( "extra", "" ), [ "customer-email" ], TestData.Context() );

		stores.InfoStore.AddStep( "loose", new ObjectDefinition( "one", "" ), [ ], TestData.Context() );
		stores.InfoStore.AddStep( "base", new ObjectDefinition( "create", "" ), [ ], TestData.Context() );
		stores.InfoStore.AddStep( "base", new ObjectDefinition( "seed", "" ), [ "create" ], TestData.Context() );
		stores.InfoStore.AddStep( "customer-email", new ObjectDefinition( "add", "" ), [ ], TestData.Context() );
		stores.InfoStore.AddStep( "extra", new ObjectDefinition( "go", "" ), [ ], TestData.Context() );

		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.07", "", "" ), TestData.Context() );
		stores.InfoStore.AddRelease( new ReleaseDefinition( "2026.08", "", "2026.07" ), TestData.Context() );
		stores.InfoStore.AddReleasePlan( "2026.07", "base", TestData.Context() );
		stores.InfoStore.AddReleasePlan( "2026.07", "customer-email", TestData.Context() );
		stores.InfoStore.AddReleasePlan( "2026.08", "extra", TestData.Context() );
		stores.InfoStore.SetReleaseContext( "2026.07", TestData.Context() );

		return project;
	}

	/// <summary>One of each repairable problem, and the two a reindex leaves.</summary>
	private static void Break( TempProject project )
	{
		File.WriteAllText( IndexFile( project ), "mangled by a bad merge" );

		Directory.Move( PlanDirectory( project, "2026.08", "extra" ), Path.Combine( project.Root, "plans", "extra" ) );
		Rewrite( StepFile( project, "2026.07", "base", "seed" ), "\"name\": \"seed\"", "\"name\": \"sown\"" );
		Rewrite( StepFile( project, "2026.07", "base", "seed" ), "\"create\"", "\"create\", \"old-name\"" );
		Rewrite( PlanFile( project, "", "loose" ), "\"name\": \"loose\"", "\"name\": \"tight\"" );
		Move( StepDirectory( project, "2026.07", "customer-email", "add" ), "bad step" );
	}

	private static IReadOnlyList<ProjectFinding> Check( TempProject project )
	{
		using FileSystemProjectStores stores = project.Open();

		return stores.Check();
	}

	private static ( string, string, string, string, bool ) Fields( ProjectFinding finding )
		=> ( finding.Check, finding.Release, finding.Plan, finding.Step, finding.FixedByReindex );

	private static string IndexFile( TempProject project ) => Path.Combine( project.Root, "plan-index" );

	private static string PlanDirectory( TempProject project, string release, string plan )
		=> project.Open().Paths.PlanDirectory( release, plan );

	private static string PlanFile( TempProject project, string release, string plan )
		=> Path.Combine( PlanDirectory( project, release, plan ), "plan.json" );

	private static string StepDirectory( TempProject project, string release, string plan, string step )
		=> project.Open().Paths.StepDirectory( release, plan, step );

	private static string StepFile( TempProject project, string release, string plan, string step )
		=> Path.Combine( StepDirectory( project, release, plan, step ), "step.json" );

	private static string ReleaseFile( TempProject project, string release )
		=> project.Open().Paths.ReleaseFile( release );

	private static void CopyDirectory( string source, string target )
	{
		foreach( string directory in Directory.GetDirectories( source, "*", SearchOption.AllDirectories ).Prepend( source ) )
			Directory.CreateDirectory( Path.Combine( target, Path.GetRelativePath( source, directory ) ) );

		foreach( string file in Directory.GetFiles( source, "*", SearchOption.AllDirectories ) )
			File.Copy( file, Path.Combine( target, Path.GetRelativePath( source, file ) ) );
	}

	private static void Move( string directory, string newName )
		=> Directory.Move( directory, Path.Combine( Path.GetDirectoryName( directory )!, newName ) );

	private static void Rewrite( string file, string from, string to )
	{
		string text = File.ReadAllText( file );

		Assert.Contains( from, text );
		File.WriteAllText( file, text.Replace( from, to ) );
	}

	private static Dictionary<string, string> Hashes( string root )
		=> Directory.GetFiles( root, "*", SearchOption.AllDirectories )
			.ToDictionary(
				x => Path.GetRelativePath( root, x ),
				x => Convert.ToHexString( SHA256.HashData( File.ReadAllBytes( x ) ) )
			);
}
