using System.Reflection;

namespace Pug.Sqetch.DatabaseDrivers.Ado;

/// <summary>
/// Reads a driver's journaling template from its assembly: one embedded '.sql' resource per slot,
/// given the logical name 'journaling/&lt;slot&gt;.sql' by the driver's project file.
/// </summary>
public static class EmbeddedJournalingTemplate
{
	private const string Prefix = "journaling/";
	private const string Extension = ".sql";

	public static IReadOnlyDictionary<string, string> Load( Assembly assembly )
	{
		ArgumentNullException.ThrowIfNull( assembly );

		Dictionary<string, string> template = new( StringComparer.Ordinal );

		foreach( string resource in assembly.GetManifestResourceNames() )
		{
			if( !resource.StartsWith( Prefix, StringComparison.Ordinal ) ||
				!resource.EndsWith( Extension, StringComparison.Ordinal ) )
				continue;

			using Stream stream = assembly.GetManifestResourceStream( resource )!;
			using StreamReader reader = new( stream );

			template[resource[Prefix.Length..^Extension.Length]] = reader.ReadToEnd();
		}

		return template;
	}
}
