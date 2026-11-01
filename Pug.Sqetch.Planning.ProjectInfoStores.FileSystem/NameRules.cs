using System.Text.RegularExpressions;

namespace Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

public static partial class NameRules
{
	// Names become path segments; the charset must exclude path separators, leading dots
	// (hidden files / traversal) and trailing dots (invalid on Windows).
	[GeneratedRegex( @"^[A-Za-z0-9](?:[A-Za-z0-9._+()@#-]{0,126}[A-Za-z0-9_+()@#-])?$" )]
	private static partial Regex ValidName();

	public static bool IsValid( string name )
		=> ValidName().IsMatch( name );

	public static void Ensure( string name, string kind )
	{
		ArgumentNullException.ThrowIfNull( name );

		if( !IsValid( name ) )
			throw new ArgumentException(
				$"{kind} name '{name}' is not a valid name; use letters, digits or '-_+()@#.', " +
				"starting with a letter or digit and not ending with '.'" );
	}
}
