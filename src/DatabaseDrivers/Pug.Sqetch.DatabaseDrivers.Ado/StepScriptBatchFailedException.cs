using Pug.Sqetch.DatabaseDriver;

namespace Pug.Sqetch.DatabaseDrivers.Ado;

/// <summary>
/// One batch of a step script split into several failed. Names the batch, since the provider's own
/// message describes the statement but not where in the script it was.
/// </summary>
public class StepScriptBatchFailedException
	: DatabaseDriverException
{
	public int Batch { get; }

	public int Batches { get; }

	public StepScriptBatchFailedException( int batch, int batches, Exception innerException )
		: base( $"batch {batch} of {batches} failed: {innerException.Message}", innerException )
	{
		Batch = batch;
		Batches = batches;
	}
}
