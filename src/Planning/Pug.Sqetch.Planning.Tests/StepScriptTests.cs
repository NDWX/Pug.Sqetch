using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;
using Pug.Sqetch.Tests.Planning.Stores.FileSystem;

namespace Pug.Sqetch.Planning.Tests;

/// <summary>
/// Pins the ownership and verification contract of <see cref="IReadOnlyProject.GetStepScripts"/>
/// and <see cref="IReadOnlyProject.VerifyStepScripts"/>: the caller owns the returned streams,
/// and missing script files surface as <see cref="MissingStepScriptsException"/>.
/// </summary>
public class StepScriptTests
{
	[Fact]
	public void StepScriptsAreVerifiedAndOwnedByTheCaller()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();

		using IProject project = ProjectFactory.Create(
			stores.InfoStore,
			stores.ScriptsStore,
			new ReleaseChainDependencyDeterminator( stores.InfoStore ),
			TestData.User
		);

		project.Add( new PlanDefinition( "plan-a", "", [ ] ) );
		StepScriptKeys keys = project.Add( new StepDefinition( "plan-a", "step-1", "", [ ] ), "plan-a" );

		// no script files exist yet: every kind is reported missing
		MissingStepScriptsException missing =
			Assert.Throws<MissingStepScriptsException>( () => project.GetStepScripts( "plan-a", "step-1" ) );

		Assert.Equal( [ "deploy", "verify", "rollback" ], missing.Scripts );
		Assert.Throws<MissingStepScriptsException>( () => project.VerifyStepScripts( "plan-a" ) );

		WriteScript( temp.Root, keys.DeployScript, "-- deploy" );
		WriteScript( temp.Root, keys.VerifyScript, "-- verify" );
		WriteScript( temp.Root, keys.RollbackScript, "-- rollback" );

		project.VerifyStepScripts( "plan-a" );

		// the returned streams are open and readable; the caller disposes them
		using( StepScripts scripts = project.GetStepScripts( "plan-a", "step-1" ) )
		{
			Assert.Equal( "-- deploy", new StreamReader( scripts.DeployScript! ).ReadToEnd() );
			Assert.Equal( "-- verify", new StreamReader( scripts.VerifyScript! ).ReadToEnd() );
			Assert.Equal( "-- rollback", new StreamReader( scripts.RollbackScript! ).ReadToEnd() );
		}

		File.Delete( ScriptPath( temp.Root, keys.VerifyScript ) );

		missing = Assert.Throws<MissingStepScriptsException>( () => project.VerifyStepScripts( "plan-a" ) );
		Assert.Equal( [ "verify" ], missing.Scripts );
		Assert.Equal( "step-1", missing.Step );
		Assert.Equal( "plan-a", missing.Plan );
	}

	[Fact]
	public void UnknownPlanOrStepIsReportedAsSuch()
	{
		using TempProject temp = TempProject.Create();
		using FileSystemProjectStores stores = temp.Open();

		using IProject project = ProjectFactory.Create(
			stores.InfoStore,
			stores.ScriptsStore,
			new ReleaseChainDependencyDeterminator( stores.InfoStore ),
			TestData.User
		);

		project.Add( new PlanDefinition( "plan-a", "", [ ] ) );

		Assert.Throws<UnknownPlanException>( () => project.GetStepScripts( "no-such-plan", "step-1" ) );
		Assert.Throws<UnknownStepException>( () => project.GetStepScripts( "plan-a", "no-such-step" ) );
	}

	private static string ScriptPath( string root, string key )
		=> Path.Combine( root, key.Replace( '/', Path.DirectorySeparatorChar ) );

	private static void WriteScript( string root, string key, string content )
		=> File.WriteAllText( ScriptPath( root, key ), content );
}