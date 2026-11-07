using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

/// <summary>
/// The store's half of <c>project check</c>. Reads what <see cref="PlanIndex.Compute"/> and
/// <see cref="StepFolders.Inspect"/> say a reindex would write, and reports the difference; writes
/// nothing, and reads the index itself so a missing or corrupt one is a finding, not an exception.
/// </summary>
internal static class ProjectChecker
{
	public static IReadOnlyList<ProjectFinding> Check( ProjectDocument project, ProjectPaths paths )
	{
		List<ProjectFinding> findings = [ ];

		if( !NameRules.IsValid( project.Name ) )
			findings.Add( InvalidName( "", "", "", $"Project name '{project.Name}' is not a valid name." ) );

		CheckReleases( paths, findings );

		PlanIndexComputation computation = PlanIndex.Compute( paths, findings );

		CheckPlanDuplicates( paths, computation, findings );

		CheckIndex( paths, computation, findings );

		foreach( PlanFolder folder in computation.Folders )
			CheckPlan( paths, folder, findings );

		return findings
			.OrderBy( x => x.Release, StringComparer.Ordinal )
			.ThenBy( x => x.Plan, StringComparer.Ordinal )
			.ThenBy( x => x.Step, StringComparer.Ordinal )
			.ThenBy( x => x.Check, StringComparer.Ordinal )
			.ThenBy( x => x.Message, StringComparer.Ordinal )
			.ToList();
	}

	private static void CheckReleases( ProjectPaths paths, List<ProjectFinding> findings )
	{
		List<(string Directory, string Folder, string? Name)> releases = [ ];

		foreach( string releaseDirectory in paths.EnumerateReleaseDirectories( string.Empty ) )
		{
			string folder = Path.GetFileName( releaseDirectory );
			string file = Path.Combine( releaseDirectory, FileNames.ReleaseFile );

			ReleaseDocument document;

			try
			{
				document = JsonFiles.Read<ReleaseDocument>( file );

				DocumentCompleteness.Ensure( document, file );
			}
			catch( ProjectStoreException exception )
			{
				findings.Add(
					new ProjectFinding(
						ProjectChecks.DocumentUnreadable,
						folder,
						"",
						"",
						$"'{paths.RelativeToRoot( file )}' cannot be parsed: {PlanIndex.Describe( exception )}"
					)
				);

				releases.Add( ( releaseDirectory, folder, null ) );

				continue;
			}

			releases.Add( ( releaseDirectory, folder, document.Name ) );

			foreach( string name in new[] { folder, document.Name }.Distinct( StringComparer.Ordinal ) )
			{
				if( !NameRules.IsValid( name ) )
					findings.Add( InvalidName( folder, "", "", $"Release name '{name}' is not a valid name." ) );
			}

			if( !string.Equals( document.Name, folder, StringComparison.Ordinal ) )
				findings.Add(
					new ProjectFinding(
						ProjectChecks.ReleaseNameMismatch,
						folder,
						"",
						"",
						$"release.json names the release '{document.Name}', but its folder is '{folder}'."
					)
				);
		}

		foreach( string orphan in paths.EnumerateReleaseDirectoriesWithoutReleaseFile() )
			findings.Add(
				new ProjectFinding(
					ProjectChecks.OrphanFolder,
					Path.GetFileName( orphan ),
					"",
					"",
					$"The folder '{paths.RelativeToRoot( orphan )}' has no {FileNames.ReleaseFile}."
				)
			);

		// a release's name is its folder's and its release.json's, and neither may be told apart
		// from another's by case alone; the first folder of a group (by path) is the one kept
		HashSet<string> reported = new( StringComparer.Ordinal );

		foreach( IGrouping<string, (string Directory, string Folder, string? Name)> group in
				releases.GroupBy( x => x.Folder, StringComparer.OrdinalIgnoreCase )
					.Concat( releases.Where( x => x.Name is not null ).GroupBy( x => x.Name!, StringComparer.OrdinalIgnoreCase ) ) )
		{
			(string Directory, string Folder, string? Name)[] copies = group
				.OrderBy( x => paths.RelativeToRoot( x.Directory ), StringComparer.Ordinal )
				.ToArray();

			foreach( (string directory, string folder, string? _) in copies.Skip( 1 ) )
			{
				if( reported.Add( directory ) )
					findings.Add(
						new ProjectFinding(
							ProjectChecks.ReleaseDuplicate,
							folder,
							"",
							"",
							$"The release '{group.Key}' is also at '{paths.RelativeToRoot( copies[0].Directory )}'; " +
							$"this copy is at '{paths.RelativeToRoot( directory )}'."
						)
					);
			}
		}
	}

	private static void CheckPlanDuplicates(
		ProjectPaths paths,
		PlanIndexComputation computation,
		List<ProjectFinding> findings
	)
	{
		foreach( IGrouping<string, PlanFolder> group in
				computation.Folders.GroupBy( x => x.Entry.Name, StringComparer.OrdinalIgnoreCase ).Where( x => x.Count() > 1 ) )
		{
			PlanFolder[] copies = group.OrderBy( x => paths.RelativeToRoot( x.Directory ), StringComparer.Ordinal ).ToArray();

			foreach( PlanFolder copy in copies.Skip( 1 ) )
				findings.Add(
					new ProjectFinding(
						ProjectChecks.PlanDuplicate,
						copy.Release,
						copy.FolderName,
						"",
						$"The plan '{group.Key}' is also at '{paths.RelativeToRoot( copies[0].Directory )}'; " +
						$"this copy is at '{paths.RelativeToRoot( copy.Directory )}'."
					)
				);
		}
	}

	private static void CheckIndex(
		ProjectPaths paths,
		PlanIndexComputation computation,
		List<ProjectFinding> findings
	)
	{
		PlanIndex? index = PlanIndex.TryLoad( paths.PlanIndexFile, out bool missing );

		if( index is null )
		{
			findings.Add(
				missing ?
					new ProjectFinding(
						ProjectChecks.IndexMissing,
						"",
						"",
						"",
						$"The plan index '{FileNames.PlanIndexFile}' does not exist.",
						true
					) :
					new ProjectFinding(
						ProjectChecks.IndexUnreadable,
						"",
						"",
						"",
						$"The plan index '{FileNames.PlanIndexFile}' cannot be parsed.",
						true
					)
			);

			return;
		}

		IReadOnlyDictionary<string, PlanIndexEntry> expected = computation.Entries;

		foreach( PlanIndexEntry entry in index.Entries )
		{
			if( !expected.TryGetValue( entry.Name, out PlanIndexEntry? wanted ) )
			{
				findings.Add( Drift( entry.Release, entry.Name, "The plan index has an entry for a plan with no plan folder." ) );

				continue;
			}

			if( !string.Equals( entry.Name, wanted.Name, StringComparison.Ordinal ) )
				findings.Add(
					Drift(
						wanted.Release,
						entry.Name,
						$"The plan index names the plan '{entry.Name}', but plan.json names it '{wanted.Name}'."
					)
				);

			if( !string.Equals( entry.Release, wanted.Release, StringComparison.Ordinal ) )
				findings.Add(
					Drift(
						wanted.Release,
						entry.Name,
						$"The plan index puts the plan in release '{entry.Release}', but its folder is in release '{wanted.Release}'."
					)
				);

			if( !entry.Dependencies.SequenceEqual( wanted.Dependencies, StringComparer.Ordinal ) )
				findings.Add(
					Drift(
						wanted.Release,
						entry.Name,
						$"The plan index lists dependencies [{string.Join( ", ", entry.Dependencies )}], " +
						$"but plan.json declares [{string.Join( ", ", wanted.Dependencies )}]."
					)
				);
		}

		foreach( PlanIndexEntry wanted in expected.Values.Where( x => !index.Contains( x.Name ) ) )
			findings.Add( Drift( wanted.Release, wanted.Name, "The plan has a plan folder but no entry in the plan index." ) );
	}

	private static void CheckPlan( ProjectPaths paths, PlanFolder folder, List<ProjectFinding> findings )
	{
		string release = folder.Release;
		string plan = folder.FolderName;

		if( !NameRules.IsValid( plan ) )
			findings.Add( InvalidName( release, plan, "", $"Plan name '{plan}' is not a valid name." ) );

		if( folder.Realigned is not null )
			findings.Add(
				new ProjectFinding(
					ProjectChecks.PlanReleaseMismatch,
					release,
					plan,
					"",
					$"plan.json names release '{folder.Document.Release}', but the plan's folder is " +
					( release.Length == 0 ? "not in a release." : $"in release '{release}'." ),
					true
				)
			);

		if( !string.Equals( folder.Document.Name, plan, StringComparison.Ordinal ) )
			findings.Add(
				new ProjectFinding(
					ProjectChecks.PlanNameMismatch,
					release,
					plan,
					"",
					$"plan.json names the plan '{folder.Document.Name}', but its folder is '{plan}'."
				)
			);

		foreach( StepFolders.Inspection step in StepFolders.Inspect(
						Path.Combine( folder.Directory, FileNames.StepsDirectory ),
						( directory, exception ) => findings.Add(
							new ProjectFinding(
								ProjectChecks.DocumentUnreadable,
								release,
								plan,
								Path.GetFileName( directory ),
								$"'{paths.RelativeToRoot( Path.Combine( directory, FileNames.StepFile ) )}' cannot be parsed: " +
								PlanIndex.Describe( exception )
							)
						)
					) )
		{
			string name = step.FolderName;

			if( !NameRules.IsValid( name ) )
				findings.Add( InvalidName( release, plan, name, $"Step name '{name}' is not a valid name." ) );

			if( step.NameMismatch )
				findings.Add(
					new ProjectFinding(
						ProjectChecks.StepNameMismatch,
						release,
						plan,
						name,
						$"step.json names the step '{step.Stored.Name}', but its folder is '{name}'.",
						true
					)
				);

			foreach( string dependency in step.UnknownDependencies )
				findings.Add(
					new ProjectFinding(
						ProjectChecks.StepDependencyUnknown,
						release,
						plan,
						name,
						$"The step requires '{dependency}', which is not a step of the plan.",
						true
					)
				);
		}
	}

	private static ProjectFinding Drift( string release, string plan, string message )
		=> new( ProjectChecks.IndexDrift, release, plan, "", message, true );

	private static ProjectFinding InvalidName( string release, string plan, string step, string message )
		=> new( ProjectChecks.InvalidName, release, plan, step, message );
}
