namespace Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

/// <summary>
/// A plan's step folders. The folder is the truth for a step's name, as a plan's folder is for its
/// release: a rename moves the folder in one step and only then rewrites <c>step.json</c>, so a
/// rename cut short leaves the old name in <c>step.json</c>, and perhaps the old name among other
/// steps' dependencies, both of which <see cref="Realign"/> repairs.
/// </summary>
internal static class StepFolders
{
	/// <summary>
	/// Each step folder with its document named after the folder. A folder only counts as a step
	/// once <c>step.json</c> exists, since scripts may be written into it first.
	/// </summary>
	public static IEnumerable<(string Directory, StepDocument Document)> Enumerate( string stepsDirectory )
	{
		if( !Directory.Exists( stepsDirectory ) )
			yield break;

		foreach( string stepDirectory in Directory.EnumerateDirectories( stepsDirectory ) )
		{
			StepDocument? document = JsonFiles.TryRead<StepDocument>( Path.Combine( stepDirectory, FileNames.StepFile ) );

			if( document is not null )
				yield return ( stepDirectory, Named( document, stepDirectory ) );
		}
	}

	public static StepDocument Named( StepDocument document, string stepDirectory )
	{
		string name = Path.GetFileName( stepDirectory );

		return string.Equals( document.Name, name, StringComparison.Ordinal ) ? document : document with { Name = name };
	}

	/// <summary>
	/// Rewrites each <c>step.json</c> whose name differs from its folder or whose dependencies name a
	/// step the plan does not have, and writes nothing when everything agrees. Dropping such a
	/// dependency is safe: deleting a step is refused while others depend on it, so only a rename cut
	/// short leaves one, and the ordering ignores it anyway.
	/// </summary>
	public static void Realign( string stepsDirectory )
	{
		if( !Directory.Exists( stepsDirectory ) )
			return;

		List<(string Directory, StepDocument Stored)> steps = [ ];

		foreach( string stepDirectory in Directory.EnumerateDirectories( stepsDirectory ) )
		{
			StepDocument? document = JsonFiles.TryRead<StepDocument>( Path.Combine( stepDirectory, FileNames.StepFile ) );

			if( document is not null )
				steps.Add( ( stepDirectory, document ) );
		}

		HashSet<string> names = new( steps.Select( x => Path.GetFileName( x.Directory ) ), StringComparer.OrdinalIgnoreCase );

		foreach( (string stepDirectory, StepDocument stored) in steps )
		{
			StepDocument aligned = Named( stored, stepDirectory );
			string[] dependencies = aligned.Dependencies.Where( names.Contains ).ToArray();

			if( ReferenceEquals( aligned, stored ) && dependencies.Length == stored.Dependencies.Length )
				continue;

			JsonFiles.Write(
				Path.Combine( stepDirectory, FileNames.StepFile ),
				aligned with { Dependencies = dependencies }
			);
		}
	}
}
