using System.Text.Json;

namespace Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

internal static class JsonFiles
{
	// camelCase, indented, LF and a trailing newline: the files are committed to version
	// control, so serialization must be deterministic to keep diffs minimal.
	public static readonly JsonSerializerOptions Options = new ()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		WriteIndented = true,
		NewLine = "\n"
	};

	public static T Read<T>( string path ) where T : class
	{
		try
		{
			using FileStream stream = File.OpenRead( path );

			return JsonSerializer.Deserialize<T>( stream, Options )
					?? throw new ProjectStoreException( $"File '{path}' contains no document." );
		}
		catch( JsonException exception )
		{
			throw new ProjectStoreException( $"File '{path}' is not a valid Sqetch document.", exception );
		}
	}

	public static T? TryRead<T>( string path ) where T : class
		=> File.Exists( path ) ? Read<T>( path ) : null;

	public static void Write<T>( string path, T document )
		=> AtomicFile.WriteAllText( path, JsonSerializer.Serialize( document, Options ) + "\n" );
}

internal static class AtomicFile
{
	public static void WriteAllText( string path, string content )
	{
		string directory = Path.GetDirectoryName( path )!;

		Directory.CreateDirectory( directory );

		string temporary = Path.Combine( directory, $".{Path.GetFileName( path )}.{Guid.NewGuid():N}.tmp" );

		try
		{
			File.WriteAllText( temporary, content );
			File.Move( temporary, path, overwrite: true );
		}
		catch
		{
			File.Delete( temporary );
			throw;
		}
	}
}
