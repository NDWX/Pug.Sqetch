namespace Pug.Sqetch.Planning.Models;

public record StepScripts( Stream? DeployScript, Stream? VerifyScript, Stream? RollbackScript ) : IDisposable,
	IAsyncDisposable
{
	public void Dispose()
	{
		DeployScript?.Dispose();
		VerifyScript?.Dispose();
		RollbackScript?.Dispose();

		GC.SuppressFinalize( this );
	}

	public async ValueTask DisposeAsync()
	{
		if( DeployScript != null ) await DeployScript.DisposeAsync();
		if( VerifyScript != null ) await VerifyScript.DisposeAsync();
		if( RollbackScript != null ) await RollbackScript.DisposeAsync();
	}
}