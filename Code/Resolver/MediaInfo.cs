using System.Threading;

namespace Bimp.Resolver;

/// <summary>
/// A user facing resolve failure ("This video is private.").
/// </summary>
public class ResolveException : Exception
{
	public ResolveException( string message ) : base( message ) { }
}

/// <summary>
/// What the host learns about a piece of media - shared with everyone through the synced queue item.
/// Contains no stream urls: those are tied to the IP that asked for them, so every client gets its own.
/// </summary>
public sealed class MediaInfo
{
	/// <summary> The extractor that understood the url (<see cref="IExtractor.Key"/>). </summary>
	public string Extractor { get; set; }

	/// <summary> Extractor specific id (YouTube video id, SoundCloud url...). </summary>
	public string Id { get; set; }

	public string Title { get; set; }

	/// <summary> Seconds, 0 if unknown. </summary>
	public float Duration { get; set; }

	public bool AudioOnly { get; set; }
	public string Thumbnail { get; set; }

	/// <summary> The stream is merged into local files by each client - it seeks by reopening at a time. </summary>
	public bool Merged { get; set; }

	/// <summary> A live stream (a Twitch channel...): played near its live edge, no seeking, no duration. </summary>
	public bool IsLive { get; set; }

	/// <summary> Video heights clients can pick, highest first. </summary>
	public List<int> Qualities { get; set; } = new();

	/// <summary> Audio tracks (dubs) clients can pick. Empty when there's no choice. </summary>
	public List<MediaAudioTrack> AudioTracks { get; set; } = new();
}

/// <summary>
/// What a client wants to play.
/// </summary>
public sealed class StreamRequest
{
	public bool AudioOnly { get; set; }

	/// <summary> The server's bimp_max_height. </summary>
	public int MaxHeight { get; set; } = 720;

	/// <summary> An exact height the client picked, 0 for automatic. </summary>
	public int Height { get; set; }

	/// <summary> Audio language the client picked, null for the original. </summary>
	public string Language { get; set; }
}

public enum StreamKind
{
	/// <summary> A url the engine plays as-is (progressive mp4/webm/mp3). </summary>
	Direct,

	/// <summary> Separate video + audio WebM files, merged locally into segment files. </summary>
	Merge,

	/// <summary> A single audio WebM, re-laid out locally (cues at the end) for audio only playback. </summary>
	Audio,

	/// <summary> A finished HLS playlist (<see cref="StreamPlan.DirectUrl"/>), remuxed locally from a start time. </summary>
	Hls,

	/// <summary> A live stream url (<see cref="StreamPlan.DirectUrl"/>: HLS, MPEG-TS, RTSP), played like a pasted live link. </summary>
	Live,
}

/// <summary>
/// One downloadable file of a stream.
/// </summary>
public sealed class StreamFile
{
	public string Url { get; set; }

	/// <summary> Bytes, 0 if unknown. </summary>
	public long Size { get; set; }

	/// <summary> Extractor specific id of the format (YouTube itag), to find the same file again after a refresh. </summary>
	public string FormatId { get; set; }

	/// <summary> "webm", or "mp4" for a fragmented MP4 (YouTube's AV1). </summary>
	public string Container { get; set; } = "webm";
}

/// <summary>
/// How a client plays a piece of media, resolved on that client.
/// </summary>
public sealed class StreamPlan
{
	public StreamKind Kind { get; set; }
	public StreamFile Video { get; set; }
	public StreamFile Audio { get; set; }

	/// <summary> A video to use instead if <see cref="Video"/> can't be read (the VP9 copy of an AV1 choice). </summary>
	public StreamFile FallbackVideo { get; set; }

	/// <summary>
	/// A video to carry on with once <see cref="Video"/> runs out: YouTube serves its AV1 4K60 to the clients that get it
	/// for only ~88 MB (about a minute), so playback switches to VP9 1440p60 at a keyframe (see WebmSegmenter).
	/// </summary>
	public StreamFile SwitchVideo { get; set; }

	/// <summary> For <see cref="StreamKind.Direct"/>, <see cref="StreamKind.Hls"/> and <see cref="StreamKind.Live"/>. </summary>
	public string DirectUrl { get; set; }
}

/// <summary>
/// Understands one site. Stateless apart from caches, safe to share.
/// </summary>
public interface IExtractor
{
	/// <summary> Short name, used in play tokens ("yt"). </summary>
	string Key { get; }

	bool CanHandle( Uri url );

	/// <summary> Host: title, duration, choices. Throws <see cref="ResolveException"/>. </summary>
	Task<MediaInfo> GetInfoAsync( Uri url, bool audioOnly, CancellationToken ct );

	/// <summary> Client: the files to play. Throws <see cref="ResolveException"/>. </summary>
	Task<StreamPlan> GetStreamsAsync( string id, StreamRequest request, CancellationToken ct );
}
