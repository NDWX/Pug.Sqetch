namespace Pug.Sqetch.Planning.Models;

/// <summary>
/// A project's complete journaling SQL: every required <see cref="JournalingSlot"/>, with the
/// statements each one holds, and whichever optional slots are set (<see cref="IsSet"/>). An instance
/// cannot exist unless all required slots are set, so anything holding one — a bundle, a deployment —
/// is known to be complete for deploying; a command needing an optional slot checks for it.
///
/// A slot may hold several statements, separated by a line whose trimmed content is exactly
/// <see cref="StatementSeparator"/>. That is the only parsing Sqetch performs on the maintainer's
/// SQL: semicolons inside string literals, dollar-quoted bodies and 'BEGIN … END' blocks are all
/// left alone. The price is that a body containing a bare ';;' alone on a line would be split
/// wrongly.
/// </summary>
public sealed class JournalingStatements
{
	/// <summary>Separates statements within one slot when alone on a line.</summary>
	public const string StatementSeparator = ";;";

	private readonly Dictionary<JournalingSlot, string> _text = new();

	private readonly Dictionary<JournalingSlot, IReadOnlyList<string>> _statements = new();

	/// <summary>
	/// Throws <see cref="ArgumentException"/> unless every required slot is set — callers facing a
	/// user check with <see cref="Missing"/> first, so they can name every unset slot at once. An
	/// optional slot that is absent, null or blank is simply unset.
	/// </summary>
	public JournalingStatements( IReadOnlyDictionary<JournalingSlot, string?> text )
	{
		ArgumentNullException.ThrowIfNull( text );

		IReadOnlyList<JournalingSlot> missing = Missing( text );

		if( missing.Count > 0 )
			throw new ArgumentException(
				$"Journaling statements are not set for: {string.Join( ", ", missing )}.",
				nameof(text)
			);

		foreach( JournalingSlot slot in JournalingSlots.All )
		{
			if( !text.TryGetValue( slot, out string? sql ) || string.IsNullOrWhiteSpace( sql ) )
				continue;

			_text[slot] = sql;
			_statements[slot] = Parse( slot, sql );
		}
	}

	/// <summary>Whether the slot holds SQL; always true for a required slot.</summary>
	public bool IsSet( JournalingSlot slot ) => _text.ContainsKey( slot );

	/// <summary>
	/// The slot's SQL exactly as the maintainer wrote it, separators included. Throws
	/// <see cref="InvalidOperationException"/> for an unset optional slot (<see cref="IsSet"/>).
	/// </summary>
	public string Text( JournalingSlot slot ) => _text.TryGetValue( slot, out string? text ) ? text : throw Unset( slot );

	/// <summary>
	/// The slot's statements, in the order they must execute. Throws
	/// <see cref="InvalidOperationException"/> for an unset optional slot (<see cref="IsSet"/>).
	/// </summary>
	public IReadOnlyList<string> Statements( JournalingSlot slot )
		=> _statements.TryGetValue( slot, out IReadOnlyList<string>? statements ) ? statements : throw Unset( slot );

	/// <summary>
	/// The required slots that are absent, null or blank, in declaration order. Optional slots are
	/// never missing. Text that is present but malformed is not reported here — <see cref="Parse"/>
	/// reports that, per slot.
	/// </summary>
	public static IReadOnlyList<JournalingSlot> Missing( IReadOnlyDictionary<JournalingSlot, string?> text )
	{
		ArgumentNullException.ThrowIfNull( text );

		return JournalingSlots.Required
			.Where( slot => !text.TryGetValue( slot, out string? sql ) || string.IsNullOrWhiteSpace( sql ) )
			.ToList();
	}

	private static InvalidOperationException Unset( JournalingSlot slot )
		=> new( $"The optional journaling slot {slot} is not set." );

	/// <summary>
	/// Splits a slot's text and applies the rules that depend on which slot it is: there must be at
	/// least one statement, and a query slot must hold exactly one, since its result set has to come
	/// from somewhere unambiguous. Throws <see cref="ArgumentException"/> otherwise.
	/// </summary>
	public static IReadOnlyList<string> Parse( JournalingSlot slot, string text )
	{
		IReadOnlyList<string> statements = Split( text );

		// no paramName on these two: they report a problem with SQL the maintainer wrote, and the
		// CLI shows the message verbatim — " (Parameter 'text')" would be noise to them
		if( statements.Count == 0 )
			throw new ArgumentException( $"{slot} holds no statement." );

		if( JournalingSlots.IsQuery( slot ) && statements.Count > 1 )
			throw new ArgumentException(
				$"{slot} is a query and must hold exactly one statement, but holds {statements.Count}."
			);

		return statements;
	}

	/// <summary>
	/// Splits text on separator lines. Blank text yields no statements; any other blank fragment is
	/// an error, so a stray or doubled separator cannot pass unnoticed. Splitting is line-based, so
	/// CRLF and lone-CR files behave identically — a delimiter match against an embedded newline
	/// would silently fail to split a file checked out on Windows.
	/// </summary>
	public static IReadOnlyList<string> Split( string text )
	{
		ArgumentNullException.ThrowIfNull( text );

		// a byte-order mark belongs to the encoding, not to the SQL; readers normally strip it, and
		// one that survives would be sent to the server as part of the first statement
		text = text.TrimStart( '﻿' );

		List<string> statements = [ ];
		List<string> lines = [ ];
		bool separated = false;

		using StringReader reader = new( text );

		while( reader.ReadLine() is { } line )
			if( line.Trim() == StatementSeparator )
			{
				separated = true;

				Add( statements, lines );
				lines.Clear();
			}
			else
				lines.Add( line );

		if( separated || lines.Exists( line => line.Trim().Length > 0 ) )
			Add( statements, lines );

		return statements;
	}

	private static void Add( List<string> statements, List<string> lines )
	{
		string statement = string.Join( "\n", lines ).Trim();

		if( statement.Length == 0 )
			throw new ArgumentException(
				$"Statement {statements.Count + 1} is empty; every '{StatementSeparator}' separator " +
				"must lie between two statements."
			);

		statements.Add( statement );
	}
}