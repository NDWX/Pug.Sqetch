using Pug.Sqetch.Models;
using Pug.Sqetch.ProjectInfoStores.FileSystem;
using Pug.Sqetch.Tests.Stores.FileSystem;

namespace Pug.Sqetch.Tests.Deployment.Cli;

/// <summary>
/// Builds the projects 'sqetch-deploy bundle' is pointed at, through the engine rather than the
/// 'sqetch' CLI: the bundle command is all these tests are about, and authoring is covered elsewhere.
/// </summary>
internal static class BundleTestProjects
{
	/// <summary>Runs <paramref name="populate"/> against the project in <paramref name="temp"/>, then releases it.</summary>
	public static void Populate( TempProject temp, Action<IProject> populate )
	{
		using FileSystemProjectStores stores = temp.Open();
		using IProject project = ProjectFactory.Create(
			stores.InfoStore, stores.ScriptsStore,
			new ReleaseChainDependencyDeterminator( stores.InfoStore ), TestData.User );

		populate( project );
	}

	/// <summary>Plan 'table' with step 'create' in finalized release 2026.07.</summary>
	public static void AddFinalizedRelease( TempProject temp, IProject project )
	{
		project.Add( new PlanDefinition( "table", "", [] ) );

		SeedScripts( temp, project.Add( new StepDefinition( "table", "create", "", [] ), "table" ), "create" );

		project.CreateRelease( new ReleaseDefinition( "2026.07", "", "" ), ["table"] );
		project.FinalizeRelease( "2026.07" );
	}

	/// <summary>Every journaling slot, which bundling requires.</summary>
	public static void SetJournaling( IProject project )
	{
		foreach( JournalingSlot slot in JournalingSlots.All )
			project.SetJournalingStatement( slot, $"select '{slot}'" );
	}

	public static void SeedScripts( TempProject temp, StepScriptKeys keys, string step )
	{
		foreach( (string key, string kind) in new[]
				{
					(keys.DeployScript, "deploy"), (keys.VerifyScript, "verify"), (keys.RollbackScript, "rollback")
				} )
			File.WriteAllText(
				Path.Combine( temp.Root, key.Replace( '/', Path.DirectorySeparatorChar ) ),
				$"-- {kind} {step}" );
	}
}
