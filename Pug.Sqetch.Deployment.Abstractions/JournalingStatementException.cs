namespace Pug.Sqetch.Deployment;

/// <summary>
/// Thrown when a project's journaling SQL fails or returns something the deployment cannot read.
/// <see cref="Slot"/> names the slot and <see cref="Statement"/> the 1-based position within it, or
/// 0 when the failure belongs to the slot as a whole: with several statements per slot, the slot
/// name alone does not say which line to look at.
/// </summary>
public class JournalingStatementException
	: DeploymentException
{
	public string Slot { get; }

	public int Statement { get; }

	public JournalingStatementException( string slot, string message )
		: this( slot, 0, message )
	{
	}

	public JournalingStatementException( string slot, int statement, string message )
		: base( Describe( slot, statement, message ) )
	{
		Slot = slot;
		Statement = statement;
	}

	public JournalingStatementException( string slot, int statement, string message, Exception innerException )
		: base( Describe( slot, statement, message ), innerException )
	{
		Slot = slot;
		Statement = statement;
	}

	private static string Describe( string slot, int statement, string message )
		=> statement > 0 ? $"Journaling statement {statement} of {slot}: {message}" : $"Journaling {slot}: {message}";
}
