using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Planning;

/// <summary>
/// Thrown when a project's journaling SQL is incomplete. <see cref="Slots"/> names every unset slot,
/// not just the first, because a maintainer setting them up wants the whole list at once.
/// </summary>
public class MissingJournalingStatementsException
	: Exception
{
	public IReadOnlyList<JournalingSlot> Slots { get; }

	public MissingJournalingStatementsException( IReadOnlyList<JournalingSlot> slots )
		: base( $"Journaling statements are not set for: {string.Join( ", ", slots )}." )
	{
		Slots = slots;
	}
}