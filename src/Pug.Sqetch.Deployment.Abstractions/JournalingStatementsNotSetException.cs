namespace Pug.Sqetch.Deployment;

/// <summary>
/// The journaling SQL lacks optional statements an operation needs, so the operation is refused
/// before touching the database rather than guessing without them — for instance a status read,
/// which without <c>JournalExists</c> could not tell a database never deployed to from a journal
/// query that failed. General on purpose: any later operation needing an optional slot raises this
/// too. Slots are named as strings, since this project references no journaling model.
/// </summary>
public class JournalingStatementsNotSetException
	: DeploymentException
{
	/// <summary>The unset slots, by name.</summary>
	public IReadOnlyList<string> Slots { get; }

	/// <summary>What the slots are needed for, phrased to follow "which", e.g. "status needs to …".</summary>
	public string Purpose { get; }

	public JournalingStatementsNotSetException( IReadOnlyList<string> slots, string purpose )
		: base( $"The journaling statements do not set {string.Join( ", ", slots )}, which {purpose}." )
	{
		Slots = slots;
		Purpose = purpose;
	}
}
