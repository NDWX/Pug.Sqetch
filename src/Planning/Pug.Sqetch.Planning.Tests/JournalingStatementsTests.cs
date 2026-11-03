using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Planning.Tests;

/// <summary>
/// The statement splitter is the only parsing Sqetch performs on the maintainer's SQL, and it runs
/// on text that arrives from a git checkout, so line endings and byte-order marks are part of its
/// contract rather than incidental detail.
/// </summary>
public class JournalingStatementsTests
{
	[Fact]
	public void BlankTextHoldsNoStatement()
	{
		Assert.Empty( JournalingStatements.Split( "" ) );
		Assert.Empty( JournalingStatements.Split( "   \n\t\n" ) );
	}

	[Fact]
	public void TextWithNoSeparatorIsOneStatement()
		=> Assert.Equal(
			[ "insert into journal( release ) values( @release )" ],
			JournalingStatements.Split( "insert into journal( release ) values( @release )\n" )
		);

	[Fact]
	public void SeparatorLinesSplitStatementsInOrder()
		=> Assert.Equal(
			[ "first", "second", "third" ],
			JournalingStatements.Split( "first\n;;\nsecond\n;;\nthird" )
		);

	/// <summary>
	/// There is no .gitattributes guarantee for anything but .sql, and a Windows checkout with
	/// core.autocrlf yields ';;\r'. Matching an embedded-newline delimiter would silently fail to
	/// split, leaving the maintainer with one malformed statement and no way to see why.
	/// </summary>
	[Theory]
	[InlineData( "first\r\n;;\r\nsecond" )]
	[InlineData( "first\r;;\rsecond" )]
	[InlineData( "first\n;;\r\nsecond" )]
	public void LineEndingsDoNotAffectSplitting( string text )
		=> Assert.Equal( [ "first", "second" ], JournalingStatements.Split( text ) );

	[Fact]
	public void SeparatorLineMayBePaddedWithWhitespace()
		=> Assert.Equal( [ "first", "second" ], JournalingStatements.Split( "first\n  \t ;;\t  \nsecond" ) );

	/// <summary>
	/// Sqetch never parses SQL, so a separator only counts when it is the whole line. Anything else
	/// is the maintainer's text and travels through untouched.
	/// </summary>
	[Fact]
	public void SeparatorNotAloneOnItsLineIsLeftAlone()
	{
		Assert.Equal( [ "select 1 ;; select 2" ], JournalingStatements.Split( "select 1 ;; select 2" ) );
		Assert.Equal( [ "do $$ begin ;; end $$" ], JournalingStatements.Split( "do $$ begin ;; end $$" ) );
	}

	[Fact]
	public void BlankLinesAndCommentsInsideAStatementSurvive()
		=> Assert.Equal(
			[ "-- note\n\ninsert into journal\nvalues( @release )" ],
			JournalingStatements.Split( "-- note\n\ninsert into journal\nvalues( @release )\n" )
		);

	[Fact]
	public void ATrailingSeparatorIsAnError()
	{
		ArgumentException error = Assert.Throws<ArgumentException>( () => JournalingStatements.Split( "first\n;;\n" ) );

		Assert.Contains( "Statement 2 is empty", error.Message );
	}

	[Fact]
	public void ConsecutiveSeparatorsAreAnError()
	{
		ArgumentException error =
			Assert.Throws<ArgumentException>( () => JournalingStatements.Split( "first\n;;\n;;\nsecond" ) );

		Assert.Contains( "Statement 2 is empty", error.Message );
	}

	[Fact]
	public void ALeadingByteOrderMarkIsNotPartOfTheFirstStatement()
		=> Assert.Equal( [ "select 1" ], JournalingStatements.Split( "﻿select 1" ) );

	// ------------------------------------------------------------------ slot rules

	[Fact]
	public void AQuerySlotMustHoldExactlyOneStatement()
	{
		ArgumentException error = Assert.Throws<ArgumentException>( () =>
			JournalingStatements.Parse( JournalingSlot.GetLatestRelease, "select 1\n;;\nselect 2" )
		);

		Assert.Contains( "GetLatestRelease is a query", error.Message );
		Assert.Contains( "holds 2", error.Message );
	}

	[Fact]
	public void ANonQuerySlotMayHoldSeveralStatements()
		=> Assert.Equal(
			2,
			JournalingStatements.Parse( JournalingSlot.PrepareJournal, "create a\n;;\ncreate b" ).Count
		);

	[Fact]
	public void ASlotMustHoldAtLeastOneStatement()
	{
		ArgumentException error = Assert.Throws<ArgumentException>( () =>
			JournalingStatements.Parse( JournalingSlot.PrepareJournal, "\n  \n" )
		);

		Assert.Contains( "PrepareJournal holds no statement", error.Message );
	}

	// ------------------------------------------------------------------ file names

	[Fact]
	public void EventSlotsAreNamedForWhatTheyRespondTo()
	{
		Assert.Equal( "OnDeployingRelease.sql", JournalingSlots.FileName( JournalingSlot.DeployingRelease ) );
		Assert.Equal( "OnRolledBackStep.sql", JournalingSlots.FileName( JournalingSlot.RolledBackStep ) );
	}

	[Fact]
	public void QueriesAndPrepareAreNotEventsAndTakeNoPrefix()
	{
		Assert.Equal( "PrepareJournal.sql", JournalingSlots.FileName( JournalingSlot.PrepareJournal ) );
		Assert.Equal( "GetLatestRelease.sql", JournalingSlots.FileName( JournalingSlot.GetLatestRelease ) );
		Assert.Equal( "GetDeployedPlans.sql", JournalingSlots.FileName( JournalingSlot.GetDeployedPlans ) );
	}

	[Fact]
	public void EverySlotHasItsOwnFileName()
	{
		List<string> names = JournalingSlots.All.Select( JournalingSlots.FileName ).ToList();

		Assert.Equal( 16, names.Count );
		Assert.Equal( names.Count, names.Distinct( StringComparer.OrdinalIgnoreCase ).Count() );
		Assert.Equal( "JournalExists.sql", JournalingSlots.FileName( JournalingSlot.JournalExists ) );
	}

	// ------------------------------------------------------------------ parameters

	/// <summary>
	/// The SQL is frozen into the bundle, so a journal shared by two projects could not otherwise
	/// tell them apart — no other parameter can stand in for it.
	/// </summary>
	[Fact]
	public void EverySlotIsGivenTheProjectName()
		=> Assert.All(
			JournalingSlots.All,
			slot => Assert.Contains( JournalingSlots.Parameters.Project, JournalingSlots.ParameterNames( slot ) )
		);

	[Fact]
	public void ParametersNarrowToTheUnitTheSlotNames()
	{
		Assert.Equal(
			[ "project", "release", "description", "utcTimestamp" ],
			JournalingSlots.ParameterNames( JournalingSlot.ReleaseDeployed )
		);

		Assert.Equal(
			[ "project", "release", "plan", "description", "utcTimestamp" ],
			JournalingSlots.ParameterNames( JournalingSlot.PlanDeployed )
		);

		Assert.Equal(
			[ "project", "release", "plan", "step", "description", "utcTimestamp" ],
			JournalingSlots.ParameterNames( JournalingSlot.StepDeployed )
		);
	}

	/// <summary>
	/// Only the slots that record an event get the host's clock: a query filters on identity alone,
	/// and prepare is DDL that records nothing. It comes last so adding it did not move any
	/// parameter a positional-placeholder driver had already bound.
	/// </summary>
	[Fact]
	public void EveryWritingSlotIsGivenTheUtcTimestampLastAndNoOtherSlotIsGivenItAtAll()
		=> Assert.All(
			JournalingSlots.All,
			slot => {
				IReadOnlyList<string> names = JournalingSlots.ParameterNames( slot );

				if( JournalingSlots.Writes( slot ) )
					Assert.Equal( JournalingSlots.Parameters.UtcTimestamp, names[^1] );
				else
					Assert.DoesNotContain( JournalingSlots.Parameters.UtcTimestamp, names );
			}
		);

	[Fact]
	public void TheQueriesAndPrepareAreNotWritingSlots()
	{
		Assert.False( JournalingSlots.Writes( JournalingSlot.GetLatestRelease ) );
		Assert.False( JournalingSlots.Writes( JournalingSlot.GetDeployedPlans ) );
		Assert.False( JournalingSlots.Writes( JournalingSlot.JournalExists ) );
		Assert.False( JournalingSlots.Writes( JournalingSlot.PrepareJournal ) );

		Assert.All(
			JournalingSlots.All.Where( slot => !JournalingSlots.IsQuery( slot ) && slot != JournalingSlot.PrepareJournal
			),
			slot => Assert.True( JournalingSlots.Writes( slot ) )
		);
	}

	/// <summary>
	/// Rollback works from journaled names; the engine has no manifest description in scope there.
	/// </summary>
	[Theory]
	[InlineData( JournalingSlot.RollingBackRelease )]
	[InlineData( JournalingSlot.RollingBackPlan )]
	[InlineData( JournalingSlot.RollingBackStep )]
	[InlineData( JournalingSlot.RolledBackStep )]
	[InlineData( JournalingSlot.RolledBackPlan )]
	[InlineData( JournalingSlot.RolledBackRelease )]
	public void RollbackSlotsAreGivenNoDescription( JournalingSlot slot )
		=> Assert.DoesNotContain(
			JournalingSlots.Parameters.Description,
			JournalingSlots.ParameterNames( slot )
		);

	[Fact]
	public void TheQueriesTakeOnlyWhatTheyFilterOn()
	{
		Assert.Equal( [ "project" ], JournalingSlots.ParameterNames( JournalingSlot.GetLatestRelease ) );
		Assert.Equal( [ "project", "release" ], JournalingSlots.ParameterNames( JournalingSlot.GetDeployedPlans ) );
		Assert.Equal( [ "project" ], JournalingSlots.ParameterNames( JournalingSlot.PrepareJournal ) );
	}

	// ------------------------------------------------------------------ slot names on a command line

	[Theory]
	[InlineData( "DeployingRelease" )]
	[InlineData( "deployingrelease" )]
	[InlineData( "  DEPLOYINGRELEASE  " )]
	public void SlotNamesResolveCaseInsensitively( string name )
	{
		Assert.True( JournalingSlots.TryParse( name, out JournalingSlot slot ) );
		Assert.Equal( JournalingSlot.DeployingRelease, slot );
	}

	[Theory]
	[InlineData( null )]
	[InlineData( "" )]
	[InlineData( "   " )]
	[InlineData( "nonsense" )]
	[InlineData( "3" )]
	[InlineData( "-1" )]
	public void UnusableSlotNamesAreRejected( string? name )
		=> Assert.False( JournalingSlots.TryParse( name, out _ ) );

	// ------------------------------------------------------------------ the complete set

	[Fact]
	public void AnIncompleteSetCannotBeConstructedAndNamesWhatIsMissing()
	{
		Dictionary<JournalingSlot, string?> text = Complete();

		text[JournalingSlot.PrepareJournal] = null;
		text[JournalingSlot.GetDeployedPlans] = "   ";
		text.Remove( JournalingSlot.DeployingPlan );

		Assert.Equal(
			[ JournalingSlot.DeployingPlan, JournalingSlot.PrepareJournal, JournalingSlot.GetDeployedPlans ],
			JournalingStatements.Missing( text )
		);

		ArgumentException error = Assert.Throws<ArgumentException>( () => new JournalingStatements( text ) );

		Assert.Contains( "DeployingPlan", error.Message );
		Assert.Contains( "PrepareJournal", error.Message );
		Assert.Contains( "GetDeployedPlans", error.Message );
	}

	[Fact]
	public void ACompleteSetKeepsTheTextVerbatimAndTheStatementsSplit()
	{
		Dictionary<JournalingSlot, string?> text = Complete();

		text[JournalingSlot.PrepareJournal] = "create table a\n;;\ncreate table b";

		JournalingStatements statements = new( text );

		Assert.Empty( JournalingStatements.Missing( text ) );
		Assert.Equal( "create table a\n;;\ncreate table b", statements.Text( JournalingSlot.PrepareJournal ) );
		Assert.Equal( [ "create table a", "create table b" ], statements.Statements( JournalingSlot.PrepareJournal ) );
		Assert.Equal( [ "-- DeployingStep" ], statements.Statements( JournalingSlot.DeployingStep ) );
	}

	// ------------------------------------------------------------------ the optional slot

	[Fact]
	public void OnlyJournalExistsIsOptionalAndRequiredHoldsTheFifteenOthers()
	{
		Assert.True( JournalingSlots.IsOptional( JournalingSlot.JournalExists ) );
		Assert.All( JournalingSlots.Required, slot => Assert.False( JournalingSlots.IsOptional( slot ) ) );
		Assert.Equal( 15, JournalingSlots.Required.Count );
		Assert.DoesNotContain( JournalingSlot.JournalExists, JournalingSlots.Required );
		Assert.Equal( 16, JournalingSlots.All.Count );
	}

	[Fact]
	public void JournalExistsIsAQueryTakingOnlyTheProject()
	{
		Assert.True( JournalingSlots.IsQuery( JournalingSlot.JournalExists ) );
		Assert.Equal( [ "project" ], JournalingSlots.ParameterNames( JournalingSlot.JournalExists ) );

		ArgumentException error = Assert.Throws<ArgumentException>( () =>
			JournalingStatements.Parse( JournalingSlot.JournalExists, "select 1\n;;\nselect 2" )
		);

		Assert.Contains( "JournalExists is a query", error.Message );
	}

	[Fact]
	public void TheOptionalSlotMayBeUnsetAndIsNeverReportedMissing()
	{
		Dictionary<JournalingSlot, string?> text = Complete();

		text.Remove( JournalingSlot.JournalExists );

		Assert.Empty( JournalingStatements.Missing( text ) );

		text[JournalingSlot.JournalExists] = "  ";

		Assert.Empty( JournalingStatements.Missing( text ) );

		JournalingStatements statements = new( text );

		Assert.False( statements.IsSet( JournalingSlot.JournalExists ) );
		Assert.True( statements.IsSet( JournalingSlot.PrepareJournal ) );
	}

	[Fact]
	public void ARequiredSlotIsStillReportedMissingWhenTheOptionalOneIsSet()
	{
		Dictionary<JournalingSlot, string?> text = Complete();

		text.Remove( JournalingSlot.PrepareJournal );

		Assert.Equal( [ JournalingSlot.PrepareJournal ], JournalingStatements.Missing( text ) );
		Assert.Throws<ArgumentException>( () => new JournalingStatements( text ) );
	}

	[Fact]
	public void TextAndStatementsOfAnUnsetOptionalSlotThrow()
	{
		Dictionary<JournalingSlot, string?> text = Complete();

		text.Remove( JournalingSlot.JournalExists );

		JournalingStatements statements = new( text );

		Assert.Throws<InvalidOperationException>( () => statements.Text( JournalingSlot.JournalExists ) );
		Assert.Throws<InvalidOperationException>( () => statements.Statements( JournalingSlot.JournalExists ) );
	}

	[Fact]
	public void ASetOptionalSlotIsReadLikeAnyOther()
	{
		JournalingStatements statements = new( Complete() );

		Assert.True( statements.IsSet( JournalingSlot.JournalExists ) );
		Assert.Equal( "-- JournalExists", statements.Text( JournalingSlot.JournalExists ) );
		Assert.Equal( [ "-- JournalExists" ], statements.Statements( JournalingSlot.JournalExists ) );
	}

	private static Dictionary<JournalingSlot, string?> Complete()
		=> JournalingSlots.All.ToDictionary( slot => slot, slot => (string?)$"-- {slot}" );
}