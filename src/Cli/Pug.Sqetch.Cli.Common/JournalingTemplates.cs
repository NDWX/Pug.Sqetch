using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Cli;

/// <summary>Where a journaling template was found, in the order a lookup tries them.</summary>
public enum JournalingTemplateLevel
{
	/// <summary>'.config/sqetch' in the project folder or a parent, up to the git work-tree root.</summary>
	Repository,

	/// <summary>'sqetch' in the user's OS configuration folder.</summary>
	User,

	/// <summary>Shipped by the database driver.</summary>
	BuiltIn
}

/// <summary>One template a lookup can see; <see cref="Shadowed"/> when a higher level has one of the same name.</summary>
public sealed record JournalingTemplateEntry(
	string Driver,
	string Name,
	JournalingTemplateLevel Level,
	string? Location,
	bool Shadowed );

/// <summary>A template resolved and loaded: every slot's SQL.</summary>
public sealed record JournalingTemplate(
	string Driver,
	string Name,
	JournalingTemplateLevel Level,
	string? Location,
	IReadOnlyDictionary<JournalingSlot, string> Statements );

/// <summary>A template that cannot be found or used; the message is written for the user.</summary>
public sealed class JournalingTemplateException( string message ) : Exception( message );

/// <summary>
/// Finds the journaling templates 'sqetch journaling init' can copy into a project. Below a
/// repository or user root they live at
/// 'database-drivers/&lt;driver&gt;/journaling-templates/&lt;template&gt;/', one file per slot named
/// as in a project's 'journaling/' folder (<see cref="JournalingSlots.FileName"/>), so a project's
/// tuned statements can be copied into a template as they are. The repository level wins over the
/// user level, which wins over the driver's built-in template: the repository holds what the team
/// agreed for that codebase. A template is used whole — never a mix of slots from two levels, whose
/// SQL could disagree about tables and columns.
/// </summary>
public sealed class JournalingTemplateSources( IDatabaseDriverRegistry drivers, string? userRoot )
{
	public const string DefaultTemplate = "default";

	private const string DriversFolder = "database-drivers";
	private const string TemplatesFolder = "journaling-templates";

	/// <summary>The user level in the OS configuration folder ('~/.config/sqetch', '%APPDATA%\sqetch', …).</summary>
	public JournalingTemplateSources( IDatabaseDriverRegistry drivers ) : this( drivers, DefaultUserRoot() )
	{
	}

	/// <summary>The user-level root this instance reads; it need not exist.</summary>
	public string? UserRoot => userRoot;

	public IEnumerable<string> Drivers => drivers.Names;

	public static string? DefaultUserRoot()
	{
		string configuration = Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData );

		return string.IsNullOrEmpty( configuration ) ? null : Path.Combine( configuration, "sqetch" );
	}

	/// <summary>
	/// The nearest '.config/sqetch' from <paramref name="projectPath"/> up to the git work-tree root
	/// (the folder holding '.git'); outside a git work tree only the project folder itself is looked
	/// at, since walking further would pick up whatever happens to sit above it.
	/// </summary>
	public static string? FindRepositoryRoot( string? projectPath )
	{
		if( projectPath is null )
			return null;

		DirectoryInfo project = new( Path.GetFullPath( projectPath ) );
		DirectoryInfo? gitRoot = null;

		for( DirectoryInfo? directory = project; directory is not null; directory = directory.Parent )
			if( Path.Exists( Path.Combine( directory.FullName, ".git" ) ) )
			{
				gitRoot = directory;

				break;
			}

		for( DirectoryInfo? directory = project; directory is not null; directory = directory.Parent )
		{
			string candidate = Path.Combine( directory.FullName, ".config", "sqetch" );

			if( Directory.Exists( candidate ) )
				return candidate;

			if( gitRoot is null || directory.FullName == gitRoot.FullName )
				break;
		}

		return null;
	}

	/// <summary>The driver's registered name, or <see cref="UnknownDatabaseDriverException"/>.</summary>
	public string ResolveDriver( string name )
		=> drivers.Create( name ).Name;

	public bool IsDriver( string name )
		=> drivers.TryCreate( name, out _ );

	/// <summary>
	/// Every template visible from <paramref name="projectPath"/> (no repository level when null),
	/// for one driver or all of them, most preferred level first.
	/// </summary>
	public IReadOnlyList<JournalingTemplateEntry> List( string? projectPath, string? driver = null )
	{
		IEnumerable<string> names = driver is null ? drivers.Names.Order( StringComparer.OrdinalIgnoreCase ) : [ ResolveDriver( driver ) ];

		List<JournalingTemplateEntry> entries = [ ];

		foreach( string name in names )
		{
			HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );

			foreach( (JournalingTemplateLevel level, string template, string? location) in Candidates( projectPath, name ) )
				entries.Add( new JournalingTemplateEntry( name, template, level, location, !seen.Add( template ) ) );
		}

		return entries;
	}

	/// <summary>
	/// Folders below a repository or user root that name no registered driver, so the caller can
	/// point out what will never be used.
	/// </summary>
	public IReadOnlyList<string> UnknownDriverFolders( string? projectPath )
		=> Roots( projectPath )
			.Select( root => Path.Combine( root.Path, DriversFolder ) )
			.Where( Directory.Exists )
			.SelectMany( Directory.EnumerateDirectories )
			.Where( folder => !drivers.TryCreate( Path.GetFileName( folder ), out _ ) )
			.ToList();

	/// <summary>Finds <paramref name="template"/> for <paramref name="driver"/> at the most preferred level and loads it.</summary>
	public JournalingTemplate Resolve( string? projectPath, string driver, string template = DefaultTemplate )
	{
		string name = ResolveDriver( driver );

		foreach( (JournalingTemplateLevel level, string candidate, string? location) in Candidates( projectPath, name ) )
			if( string.Equals( candidate, template, StringComparison.OrdinalIgnoreCase ) )
				return new JournalingTemplate(
					name,
					candidate,
					level,
					location,
					location is null ? BuiltIn( name ) : Load( location ) );

		List<string> available = Candidates( projectPath, name )
			.Select( x => x.Template )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToList();

		throw new JournalingTemplateException(
			$"no journaling template '{template}' for driver '{name}'; " +
			( available.Count == 0 ? "it has none" : $"available: {string.Join( ", ", available )}" ) );
	}

	private IEnumerable<(string Path, JournalingTemplateLevel Level)> Roots( string? projectPath )
	{
		string? repository = FindRepositoryRoot( projectPath );

		if( repository is not null )
			yield return (repository, JournalingTemplateLevel.Repository);

		if( userRoot is not null && Directory.Exists( userRoot ) )
			yield return (userRoot, JournalingTemplateLevel.User);
	}

	// most preferred first; a name may repeat across levels, and the first one wins
	private IEnumerable<(JournalingTemplateLevel Level, string Template, string? Location)> Candidates(
		string? projectPath, string driver )
	{
		foreach( (string root, JournalingTemplateLevel level) in Roots( projectPath ) )
		{
			string? driverFolder = MatchingFolder( Path.Combine( root, DriversFolder ), driver );
			string? templates = driverFolder is null ? null : Path.Combine( driverFolder, TemplatesFolder );

			if( templates is null || !Directory.Exists( templates ) )
				continue;

			foreach( string folder in Directory.EnumerateDirectories( templates ).Order( StringComparer.OrdinalIgnoreCase ) )
				yield return (level, Path.GetFileName( folder ), folder);
		}

		if( drivers.Create( driver ) is IJournalingTemplateProvider )
			yield return (JournalingTemplateLevel.BuiltIn, DefaultTemplate, null);
	}

	// driver names are case-insensitive everywhere else, so a folder named 'Postgres' still counts
	private static string? MatchingFolder( string parent, string name )
		=> Directory.Exists( parent ) ?
			Directory.EnumerateDirectories( parent )
				.FirstOrDefault( x => string.Equals( Path.GetFileName( x ), name, StringComparison.OrdinalIgnoreCase ) ) :
			null;

	private static IReadOnlyDictionary<JournalingSlot, string> Load( string folder )
	{
		Dictionary<string, JournalingSlot> slotsByFile = JournalingSlots.All.ToDictionary(
			JournalingSlots.FileName, slot => slot, StringComparer.OrdinalIgnoreCase );

		Dictionary<JournalingSlot, string> statements = [ ];
		List<string> unknown = [ ];

		foreach( string file in Directory.EnumerateFiles( folder, "*.sql" ) )
			if( slotsByFile.TryGetValue( Path.GetFileName( file ), out JournalingSlot slot ) )
				statements[slot] = File.ReadAllText( file );
			else
				unknown.Add( Path.GetFileName( file ) );

		List<string> missing = JournalingSlots.All
			.Where( slot => !statements.ContainsKey( slot ) )
			.Select( JournalingSlots.FileName )
			.ToList();

		if( unknown.Count > 0 || missing.Count > 0 )
			throw new JournalingTemplateException(
				$"journaling template '{folder}' is incomplete or has stray files; a template holds one file per slot, " +
				"named as in a project's 'journaling/' folder" +
				( missing.Count > 0 ? $"; missing: {string.Join( ", ", missing )}" : "" ) +
				( unknown.Count > 0 ? $"; not a slot: {string.Join( ", ", unknown.Order() )}" : "" ) );

		return statements;
	}

	private IReadOnlyDictionary<JournalingSlot, string> BuiltIn( string driver )
	{
		IReadOnlyDictionary<string, string> template = ( (IJournalingTemplateProvider)drivers.Create( driver ) ).GetJournalingTemplate();

		Dictionary<JournalingSlot, string> statements = [ ];

		foreach( (string name, string sql) in template )
			if( Enum.TryParse( name, out JournalingSlot slot ) && Enum.IsDefined( slot ) )
				statements[slot] = sql;
			else
				throw new JournalingTemplateException( $"driver '{driver}' ships a template for unknown slot '{name}'" );

		if( statements.Count != JournalingSlots.All.Count )
			throw new JournalingTemplateException(
				$"driver '{driver}' ships an incomplete template; missing: " +
				string.Join( ", ", JournalingSlots.All.Where( x => !statements.ContainsKey( x ) ) ) );

		return statements;
	}
}
