namespace Pug.Sqetch.Planning;

/// <summary>
/// Dependency-chronological ordering of project elements: an item always follows the items
/// it depends on, ties broken by registration timestamp then name. Only dependencies that
/// refer to items inside the given set form edges; dangling references are ignored, and a
/// dependency cycle degrades to timestamp order for the affected items rather than failing —
/// listings must not die on inconsistent data.
/// </summary>
internal static class DependencyOrdering
{
	public static List<T> Sort<T>(
		IEnumerable<T> items,
		Func<T, string> name,
		Func<T, IEnumerable<string>> dependencies,
		Func<T, DateTime> timestamp
	)
	{
		List<T> pending = items
			.OrderBy( timestamp )
			.ThenBy( name, StringComparer.OrdinalIgnoreCase )
			.ToList();

		HashSet<string> known = new( pending.Select( name ), StringComparer.OrdinalIgnoreCase );
		HashSet<string> emitted = new( StringComparer.OrdinalIgnoreCase );
		List<T> ordered = new( pending.Count );

		while( pending.Count > 0 )
		{
			// emit the earliest-registered item whose in-set dependencies are all emitted,
			// so a dependant unblocked by an emission still outranks later registrations
			int ready = -1;

			for( int index = 0; index < pending.Count; index++ )
			{
				if( dependencies( pending[index] ).All( x => !known.Contains( x ) || emitted.Contains( x ) ) )
				{
					ready = index;

					break;
				}
			}

			if( ready < 0 )
			{
				// dependency cycle: emit the remainder in timestamp order
				ordered.AddRange( pending );

				break;
			}

			ordered.Add( pending[ready] );
			emitted.Add( name( pending[ready] ) );
			pending.RemoveAt( ready );
		}

		return ordered;
	}
}