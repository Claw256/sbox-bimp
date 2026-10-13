namespace Bimp;

public static class TaskExtensions
{
	/// <summary>
	/// Run a task without awaiting it. Faults are logged at trace - callers that report their own failures
	/// (BuildAfter, StartAudioFile...) stay the one warning; this only keeps the fault observed, so it can't
	/// resurface from the finalizer thread as an unobserved-exception error.
	/// </summary>
	public static async void FireAndForget( this Task task, string what )
	{
		try { await task; }
		catch ( OperationCanceledException ) { }
		catch ( Exception e ) { Log.Trace( $"[bimp] {what}: {e.Message}" ); }
	}
}
