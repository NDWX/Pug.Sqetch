using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// Journaling SQL a maintainer could plausibly write for SQLite, used to exercise the whole contract
/// against a real database rather than against marker statements a fake interprets. One row per
/// journaled event — append-only, which Sqetch does not require but which is the easiest shape to
/// read back — and <c>@utcTimestamp</c> is stored rather than SQLite's own
/// <c>current_timestamp</c>, since recording the host's instant is what the parameter is for.
/// Microsoft.Data.Sqlite writes a <see cref="DateTime"/> as ISO-8601 text, which is what a SQLite
/// column holds a timestamp as anyway.
/// </summary>
public static class SqliteJournal
{
	private const string Table = "sqetch_journal";

	public static JournalingStatements Statements()
		=> new (
			JournalingSlots.All.ToDictionary( slot => slot, slot => (string?)Statement( slot ) ) );

	private static string Statement( JournalingSlot slot )
		=> slot switch
		{
			// two statements, so a multi-statement slot is exercised; both are idempotent, as
			// prepare runs on every deployment
			JournalingSlot.PrepareJournal =>
				$"""
				create table if not exists {Table} (
					id integer primary key autoincrement,
					project text not null,
					slot text not null,
					release text not null,
					plan text,
					step text,
					description text,
					at_utc text not null
				)
				;;
				create index if not exists {Table}_release on {Table} ( project, release )
				""",

			// the release the journal is at, and whether its completion was journaled
			JournalingSlot.GetLatestRelease =>
				$"""
				select j.release,
						( select count( * ) from {Table} c
							where c.project = @project and c.release = j.release and c.slot = 'ReleaseDeployed' )
				from {Table} j
				where j.project = @project and j.slot = 'DeployingRelease'
				order by j.id desc
				limit 1
				""",

			JournalingSlot.GetDeployedPlans =>
				$"""
				select plan from {Table}
				where project = @project and release = @release and slot = 'PlanDeployed'
				""",

			_ => Insert( slot )
		};

	/// <summary>
	/// One insert per event slot, naming only the parameters that slot carries — a statement binds
	/// what it mentions, and the unmentioned parameters are supplied regardless.
	/// </summary>
	private static string Insert( JournalingSlot slot )
	{
		IReadOnlyList<string> names = JournalingSlots.ParameterNames( slot );

		List<string> columns = ["project", "slot"];
		List<string> values = ["@project", $"'{slot}'"];

		foreach( string name in names.Where( name => name != JournalingSlots.Parameters.Project ) )
		{
			columns.Add( name == JournalingSlots.Parameters.UtcTimestamp ? "at_utc" : name );
			values.Add( $"@{name}" );
		}

		return $"insert into {Table} ( {string.Join( ", ", columns )} ) values ( {string.Join( ", ", values )} )";
	}
}
