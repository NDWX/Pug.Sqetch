using System.Diagnostics.CodeAnalysis;

namespace Pug.Sqetch.Deployment.Bundling;

public interface IBundleLayoutRegistry
{
	IEnumerable<string> Names { get; }
	void Register( string name, Func<IBundleLayout> factory );
	bool TryCreate( string name, [NotNullWhen( true )] out IBundleLayout? layout );
	IBundleLayout Create( string name );
}