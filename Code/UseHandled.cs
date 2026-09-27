namespace Bimp;

/// <summary>
/// One USE key press can reach us several ways in the same frame: the screen's own look-and-press
/// (<see cref="MediaScreen"/>), the PlayerController pressing <see cref="MediaInteract"/> when in reach,
/// and the remote's close-on-use. Whoever runs first handles it, the rest see it's taken.
/// </summary>
internal static class UseHandled
{
	static float handledAt = -1;

	/// <summary>
	/// Returns true if nobody has handled a USE press this frame (and marks it handled).
	/// </summary>
	public static bool TryConsume()
	{
		if ( handledAt == Time.Now ) return false;
		handledAt = Time.Now;
		return true;
	}
}
