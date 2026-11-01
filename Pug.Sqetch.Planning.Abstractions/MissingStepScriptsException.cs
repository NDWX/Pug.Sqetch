namespace Pug.Sqetch.Planning;

/// <summary>
/// Thrown when one or more script files of a step are missing from the scripts store.
/// <see cref="Scripts"/> names the missing script kinds ("deploy", "verify", "rollback").
/// </summary>
public class MissingStepScriptsException
	: Exception
{
	public string Plan { get; }

	public string Step { get; }

	public IReadOnlyList<string> Scripts { get; }

	public MissingStepScriptsException( string plan, string step, IReadOnlyList<string> scripts )
		: base( $"Step '{step}' of plan '{plan}' is missing scripts: {string.Join( ", ", scripts )}." )
	{
		Plan = plan;
		Step = step;
		Scripts = scripts;
	}
}
