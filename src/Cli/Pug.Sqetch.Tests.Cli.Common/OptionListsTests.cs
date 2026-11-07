using Pug.Sqetch.Cli;

namespace Pug.Sqetch.Tests.Cli.Common;

public class OptionListsTests
{
	[Fact]
	public void SplitsOnCommasAndSemicolons() => Assert.Equal( [ "a", "b", "c" ], OptionLists.Split( "a,b;c" ) );

	[Fact]
	public void TrimsEntriesAndDropsEmptyOnes() => Assert.Equal( [ "a", "b" ], OptionLists.Split( " a , ;; b ,," ) );

	[Fact]
	public void ABlankValueHasNoEntries() => Assert.Empty( OptionLists.Split( " , ; " ) );

	[Fact]
	public void NullHasNoEntries() => Assert.Empty( OptionLists.Split( null ) );
}
