using Pug.Sqetch.Deployment.Bundling;

namespace Pug.Sqetch.Deployment.Cli.Commands;

/// <summary>
/// Reads step scripts out of a bundle, at the paths its layout gives them. Scripts are keyed by plan
/// and step alone in a bundle, so the release is not used. Neither the reader nor the layout is
/// owned: the command that opened the bundle disposes it.
/// </summary>
public sealed class BundleScriptContentReader( IBundleReader reader, IBundleLayout layout ) : IScriptContentReader
{
	public string ReadDeployScript( string release, string plan, string step )
		=> Read( plan, step, StepScriptKind.Deploy );

	public string ReadRollbackScript( string release, string plan, string step )
		=> Read( plan, step, StepScriptKind.Rollback );

	private string Read( string plan, string step, StepScriptKind kind )
	{
		using Stream entry = reader.Open( layout.ScriptPath( plan, step, kind ) );
		using StreamReader content = new ( entry );

		return content.ReadToEnd();
	}
}
