namespace Pug.Sqetch.Cli;

/// <summary>
/// One table within a composite result. <c>Key</c> is the stable machine name: it prefixes every
/// row when output is piped and labels the block in CSV, so a consumer can tell two sections apart
/// without reading prose. <c>Heading</c> is the caption the interactive table carries and is
/// decoration only — never key machine output off it. A section with no rows is legitimate and
/// means "none", so a composite result keeps the same sections whatever it finds.
/// </summary>
public sealed record ResultSection(
	string Key, string Heading, string[] Headers, IReadOnlyList<string[]> Rows );
