using Pug.Sqetch.Planning.ProjectInfoStores.FileSystem;

namespace Pug.Sqetch.Cli;

/// <summary>
/// Early, option-level validation of user-chosen plan, step and release names, delegating
/// to the store's <see cref="NameRules"/> so both layers enforce the same rule.
/// </summary>
public static class NameValidation
{
	public static bool IsValid( string? name )
		=> name is null || NameRules.IsValid( name );

	public static string Error( string option )
		=> $"{option} may only contain letters, digits and any of '-_+()@#.', "
			+ "starting with a letter or digit and not ending with '.'";
}
