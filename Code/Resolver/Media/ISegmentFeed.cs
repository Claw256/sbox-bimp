namespace Bimp.Resolver.Media;

/// <summary>
/// A source of finished segment files for <see cref="SegmentPlayer"/>: merged YouTube media
/// (<see cref="WebmSegmenter"/>) or a live stream (<see cref="Live.LiveSegmenter"/>).
/// </summary>
public interface ISegmentFeed
{
	/// <summary> Live: segments appear as the stream comes in, old ones go away, there's no end to seek in. </summary>
	bool IsLive { get; }

	/// <summary>
	/// Live: how much stream time must be buffered past the start point before playback starts, so the next
	/// segment is always finished before the current one ends. Sources that deliver in chunks (HLS) need more.
	/// </summary>
	double StartBufferSeconds => 0;

	/// <summary>
	/// Live, low latency: short segments that end in filler frames instead of an overlap - join the newest one and
	/// keep close behind the live edge.
	/// </summary>
	bool LowLatency => false;

	/// <summary> The newest finished segment, -1 if none yet. </summary>
	int Newest { get; }

	/// <summary> The segment if it's finished (and still on disk), else null. </summary>
	MediaSegment TryGet( int seq );

	/// <summary> Playback reached this segment - prepare it and what follows. </summary>
	void Want( int seq );

	/// <summary> Done with a segment - its file can go. </summary>
	void Release( int seq );

	/// <summary> Something went wrong, user facing. </summary>
	string Error { get; }
}
