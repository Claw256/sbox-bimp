namespace Bimp;

/// <summary>
/// A resolved piece of media, ready to be played. Stored in the networked queue,
/// so it only contains simple serializable properties.
/// </summary>
public sealed class MediaQueueItem
{
	/// <summary>
	/// The url the user entered (YouTube page, direct file, ...).
	/// </summary>
	public string Url { get; set; }

	/// <summary>
	/// The url clients actually hand to the engine's VideoPlayer.
	/// </summary>
	public string PlayUrl { get; set; }

	public string Title { get; set; }

	/// <summary>
	/// Length in seconds, 0 if unknown.
	/// </summary>
	public float Duration { get; set; }

	public bool IsLive { get; set; }

	public bool AudioOnly { get; set; }

	/// <summary>
	/// The stream can't byte-seek, seeking is done by re-requesting it with a start offset (resolver streams).
	/// </summary>
	public bool SeekByReload { get; set; }

	/// <summary>
	/// Display name of whoever requested this.
	/// </summary>
	public string RequestedBy { get; set; }

	/// <summary>
	/// Video heights the resolver can serve, comma separated, highest first ("1080,720,480"). Empty = no choice.
	/// </summary>
	public string Qualities { get; set; }

	/// <summary>
	/// Audio tracks the resolver can serve (JSON list of <see cref="MediaAudioTrack"/>). Empty = no choice.
	/// </summary>
	public string AudioTracks { get; set; }
}
