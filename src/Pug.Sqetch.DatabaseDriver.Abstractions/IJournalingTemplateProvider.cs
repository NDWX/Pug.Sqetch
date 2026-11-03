namespace Pug.Sqetch.DatabaseDriver;

/// <summary>
/// Implemented, optionally, by a database driver factory that ships a starting set of journaling
/// statements for its engine. 'sqetch journaling init' copies it into a project, after which the
/// project owns the SQL: nothing at deploy time consults the template again.
/// </summary>
public interface IJournalingTemplateProvider
{
	/// <summary>
	/// Every journaling slot's SQL, keyed by slot name ('PrepareJournal', 'DeployingRelease', …).
	/// Names are plain strings because this contract references nothing; the caller checks them
	/// against the slots it knows and refuses a template that misses or adds one.
	/// </summary>
	IReadOnlyDictionary<string, string> GetJournalingTemplate();
}
