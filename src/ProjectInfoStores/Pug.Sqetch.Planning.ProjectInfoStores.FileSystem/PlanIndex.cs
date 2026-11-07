using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

internal sealed record PlanIndexEntry( string Name, string Release, string[] Dependencies );

/// <summary>
/// A plan folder as found on disk: the <paramref name="Release"/> its location says, the stored
/// document, the <paramref name="Realigned"/> one to write when its 'release' field disagrees
/// (<c>null</c> when it agrees) and the index <paramref name="Entry"/> it makes.
/// </summary>
internal sealed record PlanFolder(
	string Directory,
	string PlanFile,
	string Release,
	PlanDocument Document,
	PlanDocument? Realigned,
	PlanIndexEntry Entry
)
{
	public string FolderName => Path.GetFileName( Directory );
}

internal sealed record PlanIndexComputation( IReadOnlyList<PlanFolder> Folders )
{
	/// <summary>The entries the index would hold: later folders win a duplicate name, as when written.</summary>
	public IReadOnlyDictionary<string, PlanIndexEntry> Entries
	{
		get
		{
			Dictionary<string, PlanIndexEntry> entries = new( StringComparer.OrdinalIgnoreCase );

			foreach( PlanFolder folder in Folders )
				entries[folder.Entry.Name] = folder.Entry;

			return entries;
		}
	}
}

/// <summary>
/// The committed 'plan-index' file: one sorted, tab-separated line per plan ever created
/// (name, release membership, declared dependencies). Purely derived data — plan.json
/// files and folder locations remain the source of truth — kept so that locating any
/// historical plan, duplicate-name checks and reverse dependency lookups never have to
/// scan release folders. <see cref="Rebuild"/> regenerates it from the tree, and realigns a
/// plan.json whose 'release' field disagrees with the folder the plan is in.
/// </summary>
internal sealed class PlanIndex
{
	private readonly string _path;
	private readonly SortedDictionary<string, PlanIndexEntry> _entries = new( StringComparer.OrdinalIgnoreCase );

	private PlanIndex( string path )
	{
		_path = path;
	}

	public static PlanIndex Load( string path )
	{
		if( !File.Exists( path ) )
			throw new ProjectStoreException(
				$"Plan index '{path}' not found; not a Sqetch project directory, or the index needs to be rebuilt."
			);

		PlanIndex index = new( path );

		foreach( string line in File.ReadLines( path ) )
		{
			if( string.IsNullOrWhiteSpace( line ) )
				continue;

			string[] fields = line.Split( '\t' );

			if( fields.Length != 3 || fields[0].Length == 0 )
				throw new ProjectStoreException(
					$"Plan index '{path}' is corrupt at line '{line}'; rebuild the index."
				);

			index._entries[fields[0]] = new PlanIndexEntry(
				fields[0],
				fields[1],
				fields[2].Length == 0 ? [ ] : fields[2].Split( ',' )
			);
		}

		return index;
	}

	/// <summary>
	/// Reads the index, or <c>null</c> when the file is absent or cannot be parsed. For
	/// <c>project check</c>, which must report such an index rather than fail on it.
	/// </summary>
	public static PlanIndex? TryLoad( string path, out bool missing )
	{
		missing = !File.Exists( path );

		if( missing )
			return null;

		try
		{
			return Load( path );
		}
		catch( ProjectStoreException )
		{
			return null;
		}
	}

	public static PlanIndex Rebuild( ProjectPaths paths ) => Apply( paths, Compute( paths ) );

	/// <summary>
	/// What <see cref="Rebuild"/> would write, without writing: every plan folder found (the plans
	/// directory, then each release's), the index entries they make and the plan.json files whose
	/// 'release' disagrees with their folder. Shared with <c>project check</c> so the two cannot drift.
	/// A plan.json that cannot be parsed throws, unless <paramref name="problems"/> is given (by the
	/// check), in which case it and each plan folder without a plan.json are added to it as findings and
	/// skipped.
	/// </summary>
	public static PlanIndexComputation Compute( ProjectPaths paths, List<ProjectFinding>? problems = null )
	{
		List<PlanFolder> folders = [ ];

		AddFromPlansDirectory( paths, folders, paths.PlansDirectory, string.Empty, problems );

		foreach( string releaseDirectory in paths.EnumerateReleaseDirectories( string.Empty ) )
			AddFromPlansDirectory(
				paths,
				folders,
				Path.Combine( releaseDirectory, FileNames.PlansDirectory ),
				Path.GetFileName( releaseDirectory ),
				problems
			);

		return new PlanIndexComputation( folders );
	}

	public static PlanIndex Apply( ProjectPaths paths, PlanIndexComputation computation )
	{
		PlanIndex index = new( paths.PlanIndexFile );

		foreach( PlanFolder folder in computation.Folders )
		{
			if( folder.Realigned is not null )
				JsonFiles.Write( folder.PlanFile, folder.Realigned );

			index._entries[folder.Entry.Name] = folder.Entry;
		}

		index.Save();

		return index;
	}

	public bool Contains( string plan ) => _entries.ContainsKey( plan );

	public PlanIndexEntry? Get( string plan ) => _entries.GetValueOrDefault( plan );

	public IReadOnlyCollection<PlanIndexEntry> Entries => _entries.Values;

	public void Set( PlanIndexEntry entry )
	{
		_entries[entry.Name] = entry;
		Save();
	}

	public void Remove( string plan )
	{
		if( _entries.Remove( plan ) )
			Save();
	}

	private static void AddFromPlansDirectory(
		ProjectPaths paths,
		List<PlanFolder> folders,
		string plansDirectory,
		string release,
		List<ProjectFinding>? problems
	)
	{
		if( !Directory.Exists( plansDirectory ) )
			return;

		foreach( string planDirectory in Directory.EnumerateDirectories( plansDirectory ) )
		{
			string planFile = Path.Combine( planDirectory, FileNames.PlanFile );

			PlanDocument? document;

			try
			{
				document = JsonFiles.TryRead<PlanDocument>( planFile );

				// only the check asks for completeness, so reindex reads exactly as before
				if( document is not null && problems is not null )
					DocumentCompleteness.Ensure( document, planFile );
			}
			catch( ProjectStoreException exception ) when( problems is not null )
			{
				problems.Add(
					new ProjectFinding(
						ProjectChecks.DocumentUnreadable,
						release,
						Path.GetFileName( planDirectory ),
						"",
						$"'{paths.RelativeToRoot( planFile )}' cannot be parsed: {Describe( exception )}"
					)
				);

				continue;
			}

			if( document is null )
			{
				problems?.Add(
					new ProjectFinding(
						ProjectChecks.OrphanFolder,
						release,
						Path.GetFileName( planDirectory ),
						"",
						$"The folder '{paths.RelativeToRoot( planDirectory )}' has no {FileNames.PlanFile}."
					)
				);

				continue;
			}

			// the folder location, not the plan.json 'release' field, is the source of
			// truth for release membership; a command cut short after moving the folder leaves
			// the field behind, so it is realigned here, and left untouched when it agrees
			PlanDocument? realigned = string.Equals( document.Release, release, StringComparison.Ordinal ) ?
				null :
				document with
				{
					Release = release,
					ReleaseAssignment = release.Length == 0 ? null : document.ReleaseAssignment
				};

			folders.Add(
				new PlanFolder(
					planDirectory,
					planFile,
					release,
					document,
					realigned,
					new PlanIndexEntry( document.Name, release, document.Dependencies )
				)
			);
		}
	}

	internal static string Describe( ProjectStoreException exception )
		=> exception.InnerException?.Message ?? exception.Message;

	private void Save()
		=> AtomicFile.WriteAllText(
			_path,
			string.Concat(
				_entries.Values.Select( x => $"{x.Name}\t{x.Release}\t{string.Join( ',', x.Dependencies )}\n" )
			)
		);
}