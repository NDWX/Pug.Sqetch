namespace Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

/// <summary>
/// What the project check asks of a document beyond parsing: the members every read relies on.
/// A hand edit can leave valid JSON without them, which the reads would turn into a null reference
/// deep in the engine; the check reports it as an unreadable document instead. Only the check uses
/// it, so other reads and reindex behave exactly as before.
/// </summary>
internal static class DocumentCompleteness
{
	public static void Ensure( PlanDocument document, string path )
		=> Ensure(
			path,
			( document.Name, "name" ),
			( document.Dependencies, "dependencies" ),
			( document.Registration, "registration" ),
			( document.Registration?.Subject, "registration.subject" )
		);

	public static void Ensure( StepDocument document, string path )
		=> Ensure(
			path,
			( document.Name, "name" ),
			( document.Dependencies, "dependencies" ),
			( document.Registration, "registration" ),
			( document.Registration?.Subject, "registration.subject" )
		);

	public static void Ensure( ReleaseDocument document, string path )
		=> Ensure(
			path,
			( document.Name, "name" ),
			( document.Registration, "registration" ),
			( document.Registration?.Subject, "registration.subject" )
		);

	private static void Ensure( string path, params (object? Value, string Member)[] members )
	{
		List<string> missing = members.Where( member => member.Value is null ).Select( member => member.Member ).ToList();

		if( missing.Count > 0 )
			throw new ProjectStoreException(
				$"File '{path}' is not a complete Sqetch document.",
				// what the check's message shows after the file's own path
				new InvalidDataException( $"it lacks {string.Join( ", ", missing.Select( name => $"'{name}'" ) )}" )
			);
	}
}
