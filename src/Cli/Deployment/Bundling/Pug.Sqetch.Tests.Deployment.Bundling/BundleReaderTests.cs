using System.Text;

using Pug.Sqetch.Deployment.Bundling;
using Pug.Sqetch.Deployment.Bundling.BundleTypes;

namespace Pug.Sqetch.Tests.Deployment.Bundling;

public class BundleReaderTests
{
	[Theory]
	[InlineData( ZipBundleType.TypeName )]
	[InlineData( TarBundleType.TypeName )]
	[InlineData( TarGzBundleType.TypeName )]
	[InlineData( DirectoryBundleType.TypeName )]
	public void WrittenBundlesReadBackWithTheSameEntriesAndContent( string typeName )
	{
		BundleTypeRegistry registry = new();
		registry.RegisterBundleTypes();

		IBundleType type = registry.Create( typeName );
		string root = TestDirectory();
		string path = Path.Combine( root, type.DefaultOutputName( "bundle" ) );

		using( IBundleWriter writer = type.Create( path ) )
		{
			writer.Add( "manifest.json", Content( "{}" ) );
			writer.Add( "plans/table/steps/create/deploy.sql", Content( "-- deploy" ) );
		}

		int temporaries = TemporaryExtractionCount();

		using( IBundleReader reader = type.Open( path ) )
		{
			Assert.Equal(
				[ "manifest.json", "plans/table/steps/create/deploy.sql" ],
				reader.Entries.OrderBy( x => x.Length ).ToArray()
			);

			Assert.True( reader.Contains( "manifest.json" ) );
			Assert.False( reader.Contains( "plans/absent/steps/x/deploy.sql" ) );

			using( Stream entry = reader.Open( "plans/table/steps/create/deploy.sql" ) )
				using( StreamReader content = new( entry ) )
					Assert.Equal( "-- deploy", content.ReadToEnd() );

			BundlingException error =
				Assert.Throws<BundlingException>( () => reader.Open( "nope.sql" ) );

			Assert.Contains( "nope.sql", error.Message );
		}

		// archive readers clean up their temporary extraction directory, and reading
		// never removes the bundle itself
		Assert.Equal( temporaries, TemporaryExtractionCount() );
		Assert.True( File.Exists( path ) || Directory.Exists( path ) );
	}

	[Fact]
	public void DirectoryReaderLeavesTheBundleInPlace()
	{
		DirectoryBundleType type = new();
		string path = Path.Combine( TestDirectory(), "bundle" );

		using( IBundleWriter writer = type.Create( path ) )
			writer.Add( "manifest.json", Content( "{}" ) );

		using( IBundleReader reader = type.Open( path ) )
			Assert.True( reader.Contains( "manifest.json" ) );

		Assert.True( File.Exists( Path.Combine( path, "manifest.json" ) ) );
	}

	[Theory]
	[InlineData( ZipBundleType.TypeName )]
	[InlineData( TarBundleType.TypeName )]
	[InlineData( TarGzBundleType.TypeName )]
	[InlineData( DirectoryBundleType.TypeName )]
	public void MissingBundlesAreRefusedWithAFriendlyError( string typeName )
	{
		BundleTypeRegistry registry = new();
		registry.RegisterBundleTypes();

		string path = Path.Combine( TestDirectory(), "absent" );

		MissingBundleException error =
			Assert.Throws<MissingBundleException>( () => registry.Create( typeName ).Open( path ) );

		Assert.Equal( path, error.Path );
		Assert.Contains( "does not exist", error.Message );
	}

	[Fact]
	public void CorruptArchivesAreRefusedWithAFriendlyError()
	{
		string path = Path.Combine( TestDirectory(), "garbage.zip" );

		File.WriteAllText( path, "not an archive" );

		int temporaries = TemporaryExtractionCount();

		BundlingException error =
			Assert.Throws<BundlingException>( () => new ZipBundleType().Open( path ) );

		Assert.Contains( "Cannot read bundle", error.Message );

		// the failed extraction's temporary directory is cleaned up
		Assert.Equal( temporaries, TemporaryExtractionCount() );
	}

	private static MemoryStream Content( string text ) => new( Encoding.UTF8.GetBytes( text ) );

	private static int TemporaryExtractionCount()
		=> Directory.GetDirectories( Path.GetTempPath(), "sqetch-bundle-*" ).Length;

	private static string TestDirectory()
	{
		string root = Path.Combine( Path.GetTempPath(), "sqetch-bundling-tests", Guid.NewGuid().ToString( "N" ) );

		Directory.CreateDirectory( root );

		return root;
	}
}