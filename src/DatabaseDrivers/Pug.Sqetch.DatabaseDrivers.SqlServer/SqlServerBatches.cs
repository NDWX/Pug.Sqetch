namespace Pug.Sqetch.DatabaseDrivers.SqlServer;

/// <summary>
/// Splits a step script into batches at 'GO' lines, the separator sqlcmd and SQL Server Management
/// Studio use. 'GO' is not T-SQL — the server would reject it — but scripts written for those tools
/// use it, and statements such as CREATE PROCEDURE, VIEW, TRIGGER and FUNCTION must each begin a
/// batch, so one step creating two procedures needs it. A line whose trimmed content is exactly 'GO',
/// in any case, separates batches; sqlcmd's 'GO &lt;count&gt;' is not supported, and nothing else in
/// the script is parsed. Batches holding only whitespace are dropped, and a script without a 'GO'
/// line runs exactly as written.
/// </summary>
public static class SqlServerBatches
{
	public static IReadOnlyList<string> Split( string script )
	{
		ArgumentNullException.ThrowIfNull( script );

		string[] lines = script.ReplaceLineEndings( "\n" ).Split( '\n' );

		// a script without separators runs exactly as written, line endings included
		if( !lines.Any( IsSeparator ) )
			return [ script ];

		List<string> batches = [ ];
		List<string> current = [ ];

		void Close()
		{
			string batch = string.Join( "\n", current );

			if( !string.IsNullOrWhiteSpace( batch ) )
				batches.Add( batch );

			current.Clear();
		}

		foreach( string line in lines )
			if( IsSeparator( line ) )
				Close();
			else
				current.Add( line );

		Close();

		// separators and nothing else leave nothing to run
		return batches;
	}

	private static bool IsSeparator( string line )
		=> string.Equals( line.Trim(), "GO", StringComparison.OrdinalIgnoreCase );
}
