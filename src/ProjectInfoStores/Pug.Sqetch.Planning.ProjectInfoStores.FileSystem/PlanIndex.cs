namespace Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

internal sealed record PlanIndexEntry( string Name, string Release, string[] Dependencies );

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

	public static PlanIndex Rebuild( ProjectPaths paths )
	{
		PlanIndex index = new( paths.PlanIndexFile );

		index.AddFromPlansDirectory( paths.PlansDirectory, string.Empty );

		foreach( string releaseDirectory in paths.EnumerateReleaseDirectories( string.Empty ) )
			index.AddFromPlansDirectory(
				Path.Combine( releaseDirectory, FileNames.PlansDirectory ),
				Path.GetFileName( releaseDirectory )
			);

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

	private void AddFromPlansDirectory( string plansDirectory, string release )
	{
		if( !Directory.Exists( plansDirectory ) )
			return;

		foreach( string planDirectory in Directory.EnumerateDirectories( plansDirectory ) )
		{
			string planFile = Path.Combine( planDirectory, FileNames.PlanFile );

			PlanDocument? document = JsonFiles.TryRead<PlanDocument>( planFile );

			if( document is null )
				continue;

			// the folder location, not the plan.json 'release' field, is the source of
			// truth for release membership; a command cut short after moving the folder leaves
			// the field behind, so it is realigned here, and left untouched when it agrees
			if( !string.Equals( document.Release, release, StringComparison.Ordinal ) )
				JsonFiles.Write(
					planFile,
					document with
					{
						Release = release,
						ReleaseAssignment = release.Length == 0 ? null : document.ReleaseAssignment
					}
				);

			_entries[document.Name] = new PlanIndexEntry( document.Name, release, document.Dependencies );
		}
	}

	private void Save()
		=> AtomicFile.WriteAllText(
			_path,
			string.Concat(
				_entries.Values.Select( x => $"{x.Name}\t{x.Release}\t{string.Join( ',', x.Dependencies )}\n" )
			)
		);
}