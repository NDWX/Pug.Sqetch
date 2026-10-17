using System.Text.Json.Nodes;
using Pug.Sqetch.ProjectInfoStores.FileSystem;

namespace Pug.Sqetch.Tests.Stores.FileSystem;

/// <summary>
/// The 'format' marker in 'sqetch-project.json': written by initialization, absent in projects
/// written before it existed, and refused when it is newer than this store reads.
/// </summary>
public class ProjectFormatTests
{
	[Fact]
	public void InitializationWritesTheFormatFirst()
	{
		using TempProject temp = TempProject.Create();

		JsonObject project = ReadProjectFile( temp );

		Assert.Equal( "format", project.First().Key );
		Assert.Equal( FileSystemProjectStores.Format, (int)project["format"]! );
	}

	[Fact]
	public void AProjectWithoutAFormatOpensAsFormatOne()
	{
		using TempProject temp = TempProject.Create();

		JsonObject project = ReadProjectFile( temp );
		project.Remove( "format" );
		WriteProjectFile( temp, project );

		using FileSystemProjectStores stores = temp.Open();

		Assert.Equal( "test-project", stores.InfoStore.GetDefinition().Name );
	}

	[Fact]
	public void ANewerFormatIsRefusedOnOpen()
	{
		using TempProject temp = TempProject.Create();

		SetFormat( temp, FileSystemProjectStores.Format + 1 );

		UnsupportedProjectFormatException refused =
			Assert.Throws<UnsupportedProjectFormatException>( () => temp.Open() );

		Assert.Equal( FileSystemProjectStores.Format + 1, refused.Format );
		Assert.Equal( FileSystemProjectStores.Format, refused.Supported );
		Assert.Contains( $"format {FileSystemProjectStores.Format + 1}", refused.Message );
		Assert.Contains( $"up to format {FileSystemProjectStores.Format}", refused.Message );
	}

	[Theory]
	[InlineData( 0 )]
	[InlineData( -1 )]
	public void ANonPositiveFormatIsRefused( int format )
	{
		using TempProject temp = TempProject.Create();

		SetFormat( temp, format );

		Assert.Throws<UnsupportedProjectFormatException>( () => temp.Open() );
	}

	private static void SetFormat( TempProject temp, int format )
	{
		JsonObject project = ReadProjectFile( temp );
		project["format"] = format;
		WriteProjectFile( temp, project );
	}

	private static string ProjectFile( TempProject temp ) => Path.Combine( temp.Root, "sqetch-project.json" );

	private static JsonObject ReadProjectFile( TempProject temp )
		=> JsonNode.Parse( File.ReadAllText( ProjectFile( temp ) ) )!.AsObject();

	private static void WriteProjectFile( TempProject temp, JsonObject project )
		=> File.WriteAllText( ProjectFile( temp ), project.ToJsonString() );
}
