using System.Text.Json;

using Pug.Sqetch.Planning;
using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Cli;

/// <summary>
/// Resolves the acting user from '.sqetch-user' files: the project directory first, then
/// the user's home directory.
/// </summary>
public static class UserIdentity
{
	private static readonly JsonSerializerOptions FileOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		WriteIndented = true
	};

	public static UserInfo Require( string projectPath )
	{
		string[] candidates =
		[
			Path.Combine( projectPath, Defaults.UserInfoStoreFileName ),
			Path.Combine(
				Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ),
				Defaults.UserInfoStoreFileName
			)
		];

		foreach( string candidate in candidates )
		{
			if( !File.Exists( candidate ) )
				continue;

			UserInfo? user = JsonSerializer.Deserialize<UserInfo>( File.ReadAllText( candidate ), FileOptions );

			if( user is not null )
				return user;
		}

		throw new UnknownUserException();
	}

	public static string Save( UserInfo user, string projectPath, bool global )
	{
		string directory = global ? Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ) : projectPath;

		File.WriteAllText(
			Path.Combine( directory, Defaults.UserInfoStoreFileName ),
			JsonSerializer.Serialize( user, FileOptions ) + "\n"
		);

		return directory;
	}
}