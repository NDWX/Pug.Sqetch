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
	/// A step folder with a <c>step.json</c>: the document as stored, as it would be after
	/// <see cref="Realign"/>, and the dependencies it names that no step folder of the plan matches.
	/// </summary>
	public sealed record Inspection(
		string Directory,
		StepDocument Stored,
		StepDocument Aligned,
		bool NameMismatch,
		string[] UnknownDependencies
	)
	{
		public string FolderName => Path.GetFileName( Directory );

		public bool NeedsRealign => NameMismatch || UnknownDependencies.Length > 0;
	}

	/// <summary>
	/// Reports, without writing, what <see cref="Realign"/> would rewrite: each step whose name
	/// differs from its folder or whose dependencies name no step folder of the plan (compared
	/// case-insensitively). Every step is listed, rewritten or not. A <c>step.json</c> that cannot be
	/// parsed throws, unless <paramref name="onUnreadable"/> is given, which is told the step folder
	/// and the failure instead, and the step is left out.
	/// </summary>
	public static IReadOnlyList<Inspection> Inspect(
		string stepsDirectory,
		Action<string, ProjectStoreException>? onUnreadable = null
	)
	{
		if( !Directory.Exists( stepsDirectory ) )
			return [ ];

		List<(string Directory, StepDocument Stored)> steps = [ ];

		foreach( string stepDirectory in Directory.EnumerateDirectories( stepsDirectory ) )
		{
			StepDocument? document;

			try
			{
				string file = Path.Combine( stepDirectory, FileNames.StepFile );

				document = JsonFiles.TryRead<StepDocument>( file );

				// only the check asks for completeness, so realign reads exactly as before
				if( document is not null && onUnreadable is not null )
					DocumentCompleteness.Ensure( document, file );
			}
			catch( ProjectStoreException exception ) when( onUnreadable is not null )
			{
				onUnreadable( stepDirectory, exception );

				continue;
			}

			if( document is not null )
				steps.Add( ( stepDirectory, document ) );
		}

		HashSet<string> names = new( steps.Select( x => Path.GetFileName( x.Directory ) ), StringComparer.OrdinalIgnoreCase );

		List<Inspection> inspections = [ ];

		foreach( (string stepDirectory, StepDocument stored) in steps )
		{
			StepDocument named = Named( stored, stepDirectory );

			inspections.Add(
				new Inspection(
					stepDirectory,
					stored,
					named with { Dependencies = named.Dependencies.Where( names.Contains ).ToArray() },
					!ReferenceEquals( named, stored ),
					named.Dependencies.Where( x => !names.Contains( x ) ).ToArray()
				)
			);
		}

		return inspections;
	}

	/// <summary>
	/// Rewrites each <c>step.json</c> whose name differs from its folder or whose dependencies name a
	/// step the plan does not have, and writes nothing when everything agrees. Dropping such a
	/// dependency is safe: deleting a step is refused while others depend on it, so only a rename cut
	/// short leaves one, and the ordering ignores it anyway.
	/// </summary>
	public static void Realign( string stepsDirectory )
	{
		foreach( Inspection step in Inspect( stepsDirectory ).Where( x => x.NeedsRealign ) )
			JsonFiles.Write( Path.Combine( step.Directory, FileNames.StepFile ), step.Aligned );
	}
}
