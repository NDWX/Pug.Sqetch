using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// The journaling template a driver ships, as the statements a deployment runs. The real-database
/// scenarios use it rather than SQL of their own, so they prove exactly what 'sqetch journaling
/// init' gives a project: one row per event in 'sqetch_journal', and queries that stay right after
/// a rollback.
/// </summary>
public static class BuiltInJournal
{
	public static JournalingStatements Statements( IJournalingTemplateProvider driver )
	{
		IReadOnlyDictionary<string, string> template = driver.GetJournalingTemplate();

		return new JournalingStatements(
			JournalingSlots.All.ToDictionary( slot => slot, slot => (string?)template[slot.ToString()] )
		);
	}
}
