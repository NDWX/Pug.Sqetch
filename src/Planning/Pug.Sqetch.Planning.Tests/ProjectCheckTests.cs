using System.Security.Cryptography;
using System.Text;

using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStore;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;
using Pug.Sqetch.Tests.Planning.Stores.FileSystem;

namespace Pug.Sqetch.Planning.Tests;

/// <summary>
/// <see cref="IReadOnlyProject.Check"/>: each broken state is written through the store directly,
/// bypassing the engine's rules that would refuse it, and must be reported on its own without hiding
/// the others.
/// </summary>
public sealed class ProjectCheckTests : IDisposable
{
	private readonly TempProject _temp;
	private readonly FileSystemProjectStores _stores;
	private readonly IProject _project;

	public ProjectCheckTests()
	{
		_temp = TempProject.Create();
		_stores = _temp.Open();

		_project = ProjectFactory.Create(
			_stores.InfoStore,
			_stores.ScriptsStore,
			new ReleaseChainDependencyDeterminator( _stores.InfoStore ),
			TestData.User
		);
	}

	public void Dispose()
	{
		_project.Dispose();
		_stores.Dispose();
		_temp.Dispose();
	}

	private IProjectInfoStore Store => _stores.InfoStore;

	private void Plan( string name, params string[] dependencies )
		=> _project.Add( new PlanDefinition( name, "", dependencies ) );

	private StepScriptKeys Step( string plan, string name, params string[] dependencies )
	{
		StepScriptKeys keys = _project.Add( new StepDefinition( plan, name, "", dependencies ), plan );

		File.WriteAllText( ScriptPath( keys.DeployScript ), "-- deploy" );
		File.WriteAllText( ScriptPath( keys.VerifyScript ), "-- verify" );
		File.WriteAllText( ScriptPath( keys.RollbackScript ), "-- rollback" );

		return keys;
	}

	private string ScriptPath( string key )
		=> Path.Combine( _temp.Root, key.Replace( '/', Path.DirectorySeparatorChar ) );

	private void Release( string name, string dependency, params string[] plans )
		=> _project.CreateRelease( new ReleaseDefinition( name, "", dependency ), plans );

	private void SetJournaling()
	{
		foreach( JournalingSlot slot in JournalingSlots.Required )
			_project.SetJournalingStatement( slot, "select 1" );
	}

	private IReadOnlyList<ProjectFinding> Check( params string[] codes )
		=> _project.Check().Where( x => codes.Length == 0 || codes.Contains( x.Check ) ).ToList();

	[Fact]
	public void ACleanProjectHasNoFindings()
	{
		Plan( "base" );
		Step( "base", "create" );
		Step( "base", "index", "create" );
		Plan( "next", "base" );
		Step( "next", "alter" );
		Plan( "later", "next" );
		Step( "later", "seed" );

		Release( "1.0", "", "base", "next" );
		_project.FinalizeRelease( "1.0" );
		Release( "1.1", "1.0", "later" );

		SetJournaling();

		Assert.Empty( _project.Check() );
	}

	[Fact]
	public void AReleasedPlanWithAnUnreleasedDependencyIsReported()
	{
		Plan( "a" );
		Plan( "b" );
		Release( "1.0", "", "a" );

		Store.UpdatePlan( new PlanDefinition( "a", "", [ "b" ] ) );

		ProjectFinding finding = Assert.Single( Check( ProjectChecks.ReleasedPlanMissingDependency ) );

		Assert.Equal( "1.0", finding.Release );
		Assert.Equal( "a", finding.Plan );
		Assert.Equal( "", finding.Step );
		Assert.False( finding.FixedByReindex );
		Assert.Contains( "'b'", finding.Message );
		Assert.Contains( "unreleased", finding.Message );
	}

	[Fact]
	public void AReleasedPlanWithADependencyInALaterReleaseIsReportedOncePerDependency()
	{
		Plan( "a" );
		Plan( "b" );
		Plan( "c" );
		Release( "1.0", "", "a" );
		Release( "1.1", "1.0", "b", "c" );

		Store.UpdatePlan( new PlanDefinition( "a", "", [ "b", "c" ] ) );

		IReadOnlyList<ProjectFinding> findings = Check( ProjectChecks.ReleasedPlanMissingDependency );

		Assert.Equal( 2, findings.Count );
		Assert.All( findings, x => Assert.Equal( ("1.0", "a"), (x.Release, x.Plan) ) );
		Assert.Contains( "'b'", findings[0].Message );
		Assert.Contains( "'c'", findings[1].Message );
		Assert.Contains( "1.1", findings[0].Message );
		Assert.Contains( "later", findings[0].Message );
	}

	[Fact]
	public void LaterReleaseAndUnreleasedDependenciesAreBothReported()
	{
		Plan( "a" );
		Plan( "b" );
		Plan( "c" );
		Plan( "d" );
		Release( "1.0", "", "a" );
		Release( "1.1", "1.0", "b" );

		Store.UpdatePlan( new PlanDefinition( "a", "", [ "b", "c", "d" ] ) );
		Store.UpdatePlan( new PlanDefinition( "b", "", [ "a" ] ) );

		IReadOnlyList<ProjectFinding> findings = Check( ProjectChecks.ReleasedPlanMissingDependency );

		Assert.Equal( 3, findings.Count );
		Assert.Contains( findings, x => x.Plan == "a" && x.Message.Contains( "'b'" ) && x.Message.Contains( "later" ) );
		Assert.Contains( findings, x => x.Plan == "a" && x.Message.Contains( "'c'" ) && x.Message.Contains( "unreleased" ) );
		Assert.Contains( findings, x => x.Plan == "a" && x.Message.Contains( "'d'" ) && x.Message.Contains( "unreleased" ) );
	}

	[Fact]
	public void ReleasesDifferingOnlyInCaseDoNotMakeCheckThrow()
	{
		Plan( "a" );
		Plan( "b" );
		Store.AddRelease( new ReleaseDefinition( "R1", "", "" ), TestData.Context() );
		Store.AddRelease( new ReleaseDefinition( "r1", "", "r1" ), TestData.Context() );
		Store.SetPlanRelease( "a", "R1", TestData.Context() );
		Store.SetPlanRelease( "b", "r1", TestData.Context() );

		IReadOnlyList<ProjectFinding> findings = _project.Check();

		Assert.NotNull( findings );
	}

	[Fact]
	public void AnUnknownPlanDependencyIsReportedNotThrown()
	{
		Plan( "a" );
		Plan( "b" );
		Release( "1.0", "", "a" );

		Store.UpdatePlan( new PlanDefinition( "a", "", [ "ghost" ] ) );
		Store.UpdatePlan( new PlanDefinition( "b", "", [ "phantom", "a" ] ) );

		IReadOnlyList<ProjectFinding> findings = Check( ProjectChecks.PlanDependencyUnknown );

		Assert.Equal( 2, findings.Count );
		Assert.Equal( ("1.0", "a"), (findings[0].Release, findings[0].Plan) );
		Assert.Contains( "'ghost'", findings[0].Message );
		Assert.Equal( ("", "b"), (findings[1].Release, findings[1].Plan) );
		Assert.Contains( "'phantom'", findings[1].Message );

		// the unknown dependency does not stop the release's own dependency check
		Assert.Empty( Check( ProjectChecks.ReleasedPlanMissingDependency ) );
	}

	[Fact]
	public void AFirstCycleIsReportedOnceFromItsSmallestName()
	{
		Plan( "c" );
		Plan( "a" );
		Plan( "b" );
		Plan( "free" );

		Store.UpdatePlan( new PlanDefinition( "c", "", [ "a" ] ) );
		Store.UpdatePlan( new PlanDefinition( "a", "", [ "b" ] ) );
		Store.UpdatePlan( new PlanDefinition( "b", "", [ "c" ] ) );

		ProjectFinding finding = Assert.Single( Check( ProjectChecks.PlanDependencyCycle ) );

		Assert.Equal( "a", finding.Plan );
		Assert.Contains( "a -> b -> c -> a", finding.Message );
	}

	[Fact]
	public void SeparateAndSelfCyclesAreEachReported()
	{
		Plan( "x" );
		Plan( "y" );
		Plan( "self" );

		Store.UpdatePlan( new PlanDefinition( "x", "", [ "y" ] ) );
		Store.UpdatePlan( new PlanDefinition( "y", "", [ "x" ] ) );
		Store.UpdatePlan( new PlanDefinition( "self", "", [ "self" ] ) );

		IReadOnlyList<ProjectFinding> findings = Check( ProjectChecks.PlanDependencyCycle );

		Assert.Equal( 2, findings.Count );
		Assert.Contains( findings, x => x.Plan == "x" && x.Message.Contains( "x -> y -> x" ) );
		Assert.Contains( findings, x => x.Plan == "self" && x.Message.Contains( "self -> self" ) );
	}

	[Fact]
	public void AReleaseWithTwoDependantsIsReported()
	{
		Store.AddRelease( new ReleaseDefinition( "1.0", "", "" ), TestData.Context() );
		Store.AddRelease( new ReleaseDefinition( "1.1", "", "1.0" ), TestData.Context() );
		Store.AddRelease( new ReleaseDefinition( "1.2", "", "1.0" ), TestData.Context() );

		ProjectFinding finding = Assert.Single( Check( ProjectChecks.ReleaseLineage ) );

		Assert.Equal( "1.0", finding.Release );
		Assert.Contains( "more than one dependant", finding.Message );
		Assert.Contains( "'1.1'", finding.Message );
		Assert.Contains( "'1.2'", finding.Message );
	}

	[Fact]
	public void AReleaseFollowingNoReleaseIsReported()
	{
		Store.AddRelease( new ReleaseDefinition( "1.0", "", "" ), TestData.Context() );
		Store.AddRelease( new ReleaseDefinition( "1.1", "", "0.9" ), TestData.Context() );

		ProjectFinding finding = Assert.Single( Check( ProjectChecks.ReleaseLineage ) );

		Assert.Equal( "1.1", finding.Release );
		Assert.Contains( "'0.9'", finding.Message );
	}

	[Fact]
	public void MoreThanOneLineageStartIsReported()
	{
		Store.AddRelease( new ReleaseDefinition( "1.0", "", "" ), TestData.Context() );
		Store.AddRelease( new ReleaseDefinition( "2.0", "", "" ), TestData.Context() );

		ProjectFinding finding = Assert.Single( Check( ProjectChecks.ReleaseLineage ) );

		Assert.Contains( "starts the lineage", finding.Message );
		Assert.Contains( "'1.0'", finding.Message );
		Assert.Contains( "'2.0'", finding.Message );
	}

	[Fact]
	public void AnUnfinalizedReleaseBeforeAFinalizedOneIsReported()
	{
		Store.AddRelease( new ReleaseDefinition( "1.0", "", "" ), TestData.Context() );
		Store.AddRelease( new ReleaseDefinition( "1.1", "", "1.0" ), TestData.Context() );
		Store.AddRelease( new ReleaseDefinition( "1.2", "", "1.1" ), TestData.Context() );
		Store.SetReleaseContext( "1.2", TestData.Context() );

		IReadOnlyList<ProjectFinding> findings = Check( ProjectChecks.ReleaseLineage );

		Assert.Equal( [ "1.0", "1.1" ], findings.Select( x => x.Release ).ToArray() );
		Assert.All( findings, x => Assert.Contains( "'1.2'", x.Message ) );
		Assert.All( findings, x => Assert.Contains( "not finalized", x.Message ) );
	}

	[Fact]
	public void AReleaseCycleIsReportedWithoutFailing()
	{
		Store.AddRelease( new ReleaseDefinition( "b", "", "a" ), TestData.Context() );
		Store.AddRelease( new ReleaseDefinition( "a", "", "b" ), TestData.Context() );

		ProjectFinding finding = Assert.Single( Check( ProjectChecks.ReleaseLineage ) );

		Assert.Equal( "a", finding.Release );
		Assert.Contains( "cycle", finding.Message );
		Assert.Contains( "a -> b -> a", finding.Message );
	}

	[Fact]
	public void MissingStepScriptsAreReportedPerStep()
	{
		Plan( "a" );
		StepScriptKeys one = Step( "a", "one" );
		Step( "a", "two" );

		// deleted before the release moves the plan's folder, which the keys still name
		File.Delete( ScriptPath( one.VerifyScript ) );
		File.Delete( ScriptPath( one.RollbackScript ) );

		Release( "1.0", "", "a" );

		ProjectFinding finding = Assert.Single( Check( ProjectChecks.StepScriptsMissing ) );

		Assert.Equal( ("1.0", "a", "one"), (finding.Release, finding.Plan, finding.Step) );
		Assert.Contains( "verify, rollback", finding.Message );
	}

	[Fact]
	public void AStepRequiringTwoStepsIsReported()
	{
		Plan( "a" );
		Step( "a", "one" );
		Step( "a", "two" );
		Step( "a", "three" );

		Store.SetStepDependencies( "a", "three", [ "one", "two" ] );

		ProjectFinding finding = Assert.Single( Check( ProjectChecks.StepMultipleDependencies ) );

		Assert.Equal( ("", "a", "three"), (finding.Release, finding.Plan, finding.Step) );
		Assert.Contains( "one, two", finding.Message );
		Assert.Contains( "sqetch plan move-step", finding.Message );
	}

	[Fact]
	public void ADependencyNamingNoStepOfThePlanIsNotCountedAsMultiple()
	{
		Plan( "a" );
		Step( "a", "one" );
		Step( "a", "two" );

		Store.SetStepDependencies( "a", "two", [ "gone", "one" ] );

		Assert.Empty( Check( ProjectChecks.StepMultipleDependencies ) );
	}

	[Fact]
	public void EveryUnsetRequiredJournalingSlotIsReported()
	{
		SetJournaling();

		// a blank file counts as unset
		Store.SetJournalingStatement( JournalingSlot.PrepareJournal, " " );
		Store.SetJournalingStatement( JournalingSlot.GetLatestRelease, "" );

		IReadOnlyList<ProjectFinding> findings = Check( ProjectChecks.JournalingUnset );

		Assert.Equal( 2, findings.Count );
		Assert.Contains( findings, x => x.Message.Contains( "'PrepareJournal'" ) );
		Assert.Contains( findings, x => x.Message.Contains( "'GetLatestRelease'" ) );
		Assert.All( findings, x => Assert.Equal( ("", "", ""), (x.Release, x.Plan, x.Step) ) );
	}

	[Fact]
	public void AnEmptyProjectReportsOnlyItsUnsetJournaling()
	{
		IReadOnlyList<ProjectFinding> findings = _project.Check();

		Assert.Equal( JournalingSlots.Required.Count, findings.Count );
		Assert.All( findings, x => Assert.Equal( ProjectChecks.JournalingUnset, x.Check ) );
	}

	[Fact]
	public void SeveralProblemsAreAllReportedInCheckOrder()
	{
		Plan( "a" );
		Plan( "b" );
		Step( "a", "one" );
		StepScriptKeys two = Step( "a", "two" );
		Step( "a", "three" );
		File.Delete( ScriptPath( two.DeployScript ) );
		Release( "1.0", "", "a" );
		Store.AddRelease( new ReleaseDefinition( "2.0", "", "" ), TestData.Context() );

		Store.UpdatePlan( new PlanDefinition( "a", "", [ "b", "ghost" ] ) );
		Store.UpdatePlan( new PlanDefinition( "b", "", [ "b" ] ) );
		Store.SetStepDependencies( "a", "three", [ "one", "two" ] );

		string[] codes = _project.Check().Select( x => x.Check ).Distinct().ToArray();

		Assert.Equal(
			[
				ProjectChecks.ReleasedPlanMissingDependency,
				ProjectChecks.PlanDependencyUnknown,
				ProjectChecks.PlanDependencyCycle,
				ProjectChecks.ReleaseLineage,
				ProjectChecks.StepScriptsMissing,
				ProjectChecks.StepMultipleDependencies,
				ProjectChecks.JournalingUnset
			],
			codes
		);
	}

	[Fact]
	public void CheckWorksOnAReadOnlyProject()
	{
		Plan( "a" );
		Step( "a", "one" );
		Release( "1.0", "", "a" );
		Store.UpdatePlan( new PlanDefinition( "a", "", [ "ghost" ] ) );

		using FileSystemProjectStores reopened = _temp.Open();

		using IReadOnlyProject readOnly = ProjectFactory.CreateReadOnly(
			reopened.InfoStore,
			reopened.ScriptsStore,
			new ReleaseChainDependencyDeterminator( reopened.InfoStore )
		);

		Assert.Equal( _project.Check(), readOnly.Check() );
		Assert.Contains( readOnly.Check(), x => x.Check == ProjectChecks.PlanDependencyUnknown );
	}

	[Fact]
	public void CheckWritesNothing()
	{
		Plan( "a" );
		Plan( "b" );
		Step( "a", "one" );
		Release( "1.0", "", "a" );
		Store.UpdatePlan( new PlanDefinition( "a", "", [ "b", "ghost" ] ) );
		Store.AddRelease( new ReleaseDefinition( "2.0", "", "" ), TestData.Context() );

		string before = Fingerprint();

		Assert.NotEmpty( _project.Check() );

		Assert.Equal( before, Fingerprint() );
	}

	private string Fingerprint()
	{
		StringBuilder builder = new();

		foreach( string file in Directory.EnumerateFiles( _temp.Root, "*", SearchOption.AllDirectories )
					.OrderBy( x => x, StringComparer.Ordinal ) )
			builder.Append( Path.GetRelativePath( _temp.Root, file ) )
				.Append( ':' )
				.Append( Convert.ToHexString( SHA256.HashData( File.ReadAllBytes( file ) ) ) )
				.Append( '\n' );

		foreach( string directory in Directory.EnumerateDirectories( _temp.Root, "*", SearchOption.AllDirectories )
					.OrderBy( x => x, StringComparer.Ordinal ) )
			builder.Append( "dir:" ).Append( Path.GetRelativePath( _temp.Root, directory ) ).Append( '\n' );

		return builder.ToString();
	}
}
