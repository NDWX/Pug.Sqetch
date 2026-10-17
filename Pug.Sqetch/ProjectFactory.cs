using Pug.Sqetch.Models;
using Pug.Sqetch.ProjectInfoStore;

namespace Pug.Sqetch;

public static class ProjectFactory
{
	public static IProject Create(
		IProjectInfoStore infoStore, IScriptsStore scriptsStore,
		IReleaseDependencyDeterminator releaseDependencyDeterminator, UserInfo userInfo )
	{
		ArgumentNullException.ThrowIfNull( userInfo );

		return new Project( infoStore, scriptsStore, releaseDependencyDeterminator, userInfo );
	}

	/// <summary>
	/// A project for reading only, which needs no user because it records no changes — what
	/// bundling uses, so a pipeline can bundle a project without a configured project user.
	/// </summary>
	public static IReadOnlyProject CreateReadOnly(
		IProjectInfoStore infoStore, IScriptsStore scriptsStore,
		IReleaseDependencyDeterminator releaseDependencyDeterminator )
	{
		return new Project( infoStore, scriptsStore, releaseDependencyDeterminator, userInfo: null );
	}
}
