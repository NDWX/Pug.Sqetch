using System.Text.RegularExpressions;

using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.DatabaseDrivers.MySql;
using Pug.Sqetch.DatabaseDrivers.PostgreSql;
using Pug.Sqetch.DatabaseDrivers.Sqlite;
using Pug.Sqetch.DatabaseDrivers.SqlServer;
using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Tests.DatabaseDrivers;

/// <summary>
/// The journaling templates the drivers ship: complete, and identical to the copies the agent skills
/// document for users of a 'sqetch' too old to have 'journaling init'.
/// </summary>
public class BuiltInJournalTemplateTests
{
	public static TheoryData<string> Drivers =>
	[
		SqliteDatabaseDriverFactory.DriverName, PostgreSqlDatabaseDriverFactory.DriverName,
		SqlServerDatabaseDriverFactory.DriverName, MySqlDatabaseDriverFactory.DriverName
	];

	private static IJournalingTemplateProvider Driver( string name )
		=> name switch
		{
			SqliteDatabaseDriverFactory.DriverName => new SqliteDatabaseDriverFactory(),
			PostgreSqlDatabaseDriverFactory.DriverName => new PostgreSqlDatabaseDriverFactory(),
			SqlServerDatabaseDriverFactory.DriverName => new SqlServerDatabaseDriverFactory(),
			MySqlDatabaseDriverFactory.DriverName => new MySqlDatabaseDriverFactory(),
			_ => throw new ArgumentOutOfRangeException( nameof(name), name, null )
		};

	[Theory]
	[MemberData( nameof(Drivers) )]
	public void EveryTemplateHoldsExactlyTheFifteenSlots( string driver )
	{
		IReadOnlyDictionary<string, string> template = Driver( driver ).GetJournalingTemplate();

		Assert.Equal(
			JournalingSlots.All.Select( slot => slot.ToString() ).Order(),
			template.Keys.Order() );

		Assert.All( template.Values, sql => Assert.False( string.IsNullOrWhiteSpace( sql ) ) );

		// creating the table and its index, as two statements except on MySQL, which has no
		// 'create index if not exists' and so declares the index inside the table
		Assert.Equal(
			driver == MySqlDatabaseDriverFactory.DriverName ? 1 : 2,
			BuiltInJournal.Statements( Driver( driver ) ).Statements( JournalingSlot.PrepareJournal ).Count );
	}

	[Theory]
	[InlineData( "sqlite", "journaling-sqlite.md" )]
	[InlineData( "postgres", "journaling-postgresql.md" )]
	[InlineData( "sqlserver", "journaling-sqlserver.md" )]
	[InlineData( "mysql", "journaling-mysql.md" )]
	public void TheSkillsReferenceMatchesTheShippedTemplate( string driver, string reference )
	{
		string path = Path.Combine(
			RepositoryRoot(), "claude", "pug-sqetch", "skills", "sqetch-new-project", "references", reference );

		// each '### <slot>' heading is followed, after its prose, by the slot's SQL in a fenced block
		Dictionary<string, string> documented = Regex
			.Matches(
				File.ReadAllText( path ).ReplaceLineEndings( "\n" ),
				@"^### (\w+)\n.*?^```sql\n(.*?)^```\n",
				RegexOptions.Multiline | RegexOptions.Singleline )
			.ToDictionary( x => x.Groups[1].Value, x => x.Groups[2].Value );

		IReadOnlyDictionary<string, string> shipped = Driver( driver ).GetJournalingTemplate();

		Assert.Equal( shipped.Keys.Order(), documented.Keys.Order() );

		foreach( (string slot, string sql) in shipped )
			Assert.True(
				sql.ReplaceLineEndings( "\n" ) == documented[slot],
				$"{reference} differs from the {driver} driver's template in slot '{slot}'" );
	}

	private static string RepositoryRoot()
	{
		for( DirectoryInfo? directory = new( AppContext.BaseDirectory ); directory is not null; directory = directory.Parent )
			if( File.Exists( Path.Combine( directory.FullName, "Pug.Sqetch.sln" ) ) )
				return directory.FullName;

		throw new InvalidOperationException( "the repository root (holding Pug.Sqetch.sln) was not found" );
	}
}
