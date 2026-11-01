using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// Journaling SQL a maintainer could plausibly write for PostgreSQL. The timestamp column is
/// <c>timestamptz</c>: the driver binds a UTC <see cref="DateTime"/>, which Npgsql sends as one, and
/// a <c>timestamp</c> column would store whatever the session's time zone made of that instant
/// instead.
/// </summary>
public static class PostgreSqlJournal
{
	private const string Table = "sqetch_journal";

	public static JournalingStatements Statements()
		=> new ( JournalingSlots.All.ToDictionary( slot => slot, slot => (string?)Statement( slot ) ) );

	private static string Statement( JournalingSlot slot )
		=> slot switch
		{
			// two statements, both idempotent, since prepare runs on every deployment
			JournalingSlot.PrepareJournal =>
				$"""
				create table if not exists {Table} (
					id bigserial primary key,
					project text not null,
					slot text not null,
					release text not null,
					plan text,
					step text,
					description text,
					at_utc timestamptz not null
				)
				;;
				create index if not exists {Table}_release on {Table} ( project, release )
				""",

			JournalingSlot.GetLatestRelease =>
				$"""
				select j.release,
						exists ( select 1 from {Table} c
									where c.project = @project and c.release = j.release
										and c.slot = 'ReleaseDeployed' )
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

	private static string Insert( JournalingSlot slot )
	{
		List<string> columns = ["project", "slot"];
		List<string> values = ["@project", $"'{slot}'"];

		foreach( string name in JournalingSlots.ParameterNames( slot )
												.Where( name => name != JournalingSlots.Parameters.Project ) )
		{
			columns.Add( name == JournalingSlots.Parameters.UtcTimestamp ? "at_utc" : name );
			values.Add( $"@{name}" );
		}

		return $"insert into {Table} ( {string.Join( ", ", columns )} ) values ( {string.Join( ", ", values )} )";
	}
}
