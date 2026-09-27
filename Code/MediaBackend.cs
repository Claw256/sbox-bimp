using System;
using Bimp.Resolver;
using Bimp.Resolver.Media;

namespace Bimp;

/// <summary>
/// Local playback of a single url or play token. Wraps the engine's <see cref="VideoPlayer"/> (video),
/// <see cref="MusicPlayer"/> (audio only, gives us a spectrum) or a <see cref="SegmentPlayer"/> (merged
/// YouTube media) behind one interface.
/// Each client owns one of these per <see cref="MediaPlayer"/>, it is never networked.
/// </summary>
public sealed class MediaBackend : IDisposable
{
	VideoPlayer video;
	MusicPlayer music;

	/// <summary> The direct video player's frames (see <see cref="VideoFrameSink"/>). </summary>
	readonly VideoFrameSink videoSink = new();
	SegmentPlayer segments;
	WebmSegmenter segmenter;
	Resolver.Live.LiveStream live;

	/// <summary>
	/// Natively resolved media being prepared (resolving, downloading the first segment).
	/// </summary>
	Task<StreamSession> preparing;
	string token;
	bool audioOnly;
	float startTime;
	bool disposed;

	/// <summary>
	/// Merged media: how long from starting the segmenter to its first segment being ready (download + write),
	/// averaged over every backend. A seek or join plans its first segment from where the synced time will be by then.
	/// </summary>
	static float firstSegmentSeconds = 1.5f;
	RealTimeSince sinceSegmenter;
	bool firstSegmentTimed;

	/// <summary> Checking / fixing an MP4 before playing it (see <see cref="Mp4ChannelFix"/>). </summary>
	bool checkingMp4;
	string localDirectory;
	readonly System.Threading.CancellationTokenSource cts = new();

	/// <summary>
	/// For streams that were started at an offset and whose clock starts at 0 - this is the offset.
	/// </summary>
	public float TimeOffset { get; private set; }

	public string Url { get; private set; }
	public bool IsAudioOnly => music is not null;

	/// <summary>
	/// Still resolving / downloading before anything can play.
	/// </summary>
	public bool IsPreparing => preparing is not null || checkingMp4;

	/// <summary>
	/// Something went wrong, user facing message.
	/// </summary>
	string error;
	public string Error { get => error ?? live?.Error ?? segments?.Error; private set => error = value; }

	/// <summary> Live: what we're connected to ("RTSP H.264 1920x1080 + G.711 mu-law"). </summary>
	public string LiveDescription => live is null ? null
		: $"{live.Description} | behind newest frame {LiveBehindNewest:0.00}s{(LiveLatency is { } l ? $", sent {l:0.00}s ago" : ", no sender clock")}{(live.Segmenter.Pcm is { } pcm ? $", audio {pcm.SampleRate}Hz {pcm.Channels}ch, A/V {pcm.Offset * 1000:+0;-0}ms ({pcm.State})" : "")}";

	/// <summary> Live, decoded audio: its stream time minus the picture's (seconds, negative = audio late). </summary>
	public double? LiveAvOffset => live?.Segmenter.Pcm?.Offset;

	/// <summary>
	/// Live: how long ago the frame on screen was sent, by the sender's clock (RTCP sender reports) - the latency
	/// from the source's output to our screen. Null without sender reports (MPEG-TS, HLS, servers that send none).
	/// </summary>
	public double? LiveLatency => live?.Segmenter.WallClockZero is { } zero && segments is { Loaded: true }
		? (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds - (zero + segments.StreamTime)
		: null;

	/// <summary> Live: stream time from the frame on screen to the newest frame received. </summary>
	public double LiveBehindNewest => live is null || segments is not { Loaded: true } ? 0 : live.Segmenter.NewestVideoTime - segments.StreamTime;

	/// <summary> Merged media opened before this media time starts from the beginning (see <see cref="StartNative"/>). </summary>
	const float FreshStartSeconds = 1.0f;

	/// <summary>
	/// Merged media: start at the keyframe before the planned time if it's at most this far behind it, else at the next
	/// one (waiting for it). Stays inside the drift tolerance (<see cref="MediaPlayer"/>, 1.5 s) with the estimate's error.
	/// </summary>
	const float MaxStartLag = 1.0f;

	/// <summary>
	/// Where the synced timeline is, while it's moving (null while paused). Merged media that starts at a later keyframe
	/// waits for it before opening its first segment.
	/// </summary>
	public float? SyncedTime { set { if ( segments is not null ) segments.SyncedTime = value; } }

	/// <summary>
	/// Playback has reached the end.
	/// </summary>
	bool finished;
	public bool Finished { get => finished || (segments?.Finished ?? false); private set => finished = value; }

	bool loaded;
	float lastPlaybackTime;

	/// <summary>
	/// Has the media actually started (metadata loaded / audio is advancing)?
	/// </summary>
	public bool Loaded => segments?.Loaded ?? (!IsPreparing && (loaded || PlaybackTime > 0.01f));

	/// <summary>
	/// Time since this backend was created.
	/// </summary>
	public RealTimeSince SinceCreated { get; private set; }

	/// <summary>
	/// Position within the media, taking the start offset into account.
	/// </summary>
	public float Time => TimeOffset + PlaybackTime;

	public float PlaybackTime => segments?.Time ?? video?.PlaybackTime ?? music?.PlaybackTime ?? 0;

	/// <summary>
	/// Duration as reported by the decoder.
	/// </summary>
	public float Duration => segments?.Duration ?? video?.Duration ?? music?.Duration ?? 0;

	public bool IsPaused => segments?.IsPaused ?? video?.IsPaused ?? music?.Paused ?? false;

	public Texture Texture => segments?.Texture ?? videoSink.Texture;
	public int Width => segments?.Width ?? (video is null ? 0 : videoSink.Width);
	public int Height => segments?.Height ?? (video is null ? 0 : videoSink.Height);

	/// <summary>
	/// Does the video have visible frames?
	/// </summary>
	public bool HasVideo => segments?.HasVideo ?? (video is not null && videoSink.Texture is not null && videoSink.Width > 4 && videoSink.Height > 4);

	/// <summary>
	/// 512 FFT values for visualizers. Empty for video playback.
	/// </summary>
	public ReadOnlySpan<float> Spectrum => music is not null ? music.Spectrum : ReadOnlySpan<float>.Empty;

	public float Amplitude => music?.Amplitude ?? 0;

	/// <summary>
	/// The decoded audio format, e.g. "48000Hz 2ch" (for diagnostics). Empty until audio has started.
	/// </summary>
	public string AudioFormat
	{
		get
		{
			var (rate, ch) = segments is not null ? segments.AudioFormat : video is not null ? (video.SampleRate, video.Channels) : music is not null ? (music.SampleRate, music.Channels) : (0, 0);
			return rate > 0 ? $"{rate}Hz {ch}ch" : "";
		}
	}

	MediaBackend() { }

	/// <summary>
	/// Start playing <paramref name="url"/>. Never throws, check <see cref="Error"/>.
	/// </summary>
	public static MediaBackend Create( string url, bool audioOnly, float timeOffset )
	{
		var b = new MediaBackend
		{
			Url = url,
			TimeOffset = timeOffset,
			SinceCreated = 0,
		};

		b.StartUrl( url, audioOnly );
		return b;
	}

	/// <summary>
	/// Resolve a play token on this client and start playing it at <paramref name="startTime"/> (media seconds).
	/// Never throws, check <see cref="Error"/>.
	/// </summary>
	public static MediaBackend CreateNative( string token, bool audioOnly, float startTime )
	{
		var b = new MediaBackend
		{
			Url = token,
			SinceCreated = 0,
			token = token,
			audioOnly = audioOnly,
			startTime = startTime,
		};

		b.preparing = StreamSessions.GetAsync( token );
		return b;
	}

	/// <summary>
	/// Connect to a live stream (RTSP tunnelled over HTTP, MPEG-TS over HTTP, HLS) and play it near its live edge.
	/// Never throws, check <see cref="Error"/>.
	/// </summary>
	public static MediaBackend CreateLive( string url, int maxHeight )
	{
		var b = new MediaBackend { Url = url, SinceCreated = 0, liveMaxHeight = maxHeight };
		try
		{
			// a live channel from an extractor (Twitch...): resolved on this client first (see StartNative)
			if ( PlayToken.IsToken( url ) )
			{
				b.token = url;
				b.preparing = StreamSessions.GetAsync( url );
				return b;
			}
			b.StartLive( url, false );
		}
		catch ( Exception e )
		{
			b.Error = e.Message;
		}
		return b;
	}

	int liveMaxHeight = 720;

	void StartLive( string url, bool hls )
	{
		live = new Resolver.Live.LiveStream( url, StreamSessions.NewCacheDirectory(), liveMaxHeight, hls );
		segments = new SegmentPlayer( live.Segmenter );
	}

	/// <summary> The height a play token asks for (its pick, else the server's limit). </summary>
	static int TokenHeight( string token, int fallback )
		=> PlayToken.Parse( token ) is { } t ? (t.Height > 0 ? t.Height : t.MaxHeight) : fallback;

	void StartUrl( string url, bool audio )
	{
		// MP4s whose header mislabels mono audio as stereo crackle in the engine - check first
		if ( Mp4ChannelFix.Applies( url ) )
		{
			_ = StartMp4( url, audio );
			return;
		}

		PlayUrlNow( url, audio );
	}

	async Task StartMp4( string url, bool audio )
	{
		checkingMp4 = true;
		try
		{
			localDirectory = StreamSessions.NewCacheDirectory();
			var path = await Mp4ChannelFix.PrepareAsync( url, localDirectory, cts.Token );
			if ( disposed ) return;

			if ( path is null ) PlayUrlNow( url, audio );
			else PlayFile( path, audio );
		}
		catch ( OperationCanceledException )
		{
		}
		catch ( Exception e )
		{
			if ( disposed ) return;
			// couldn't fix it - play it as it is rather than not at all
			Log.Warning( $"[bimp] Couldn't prepare {url}: {e.Message}" );
			PlayUrlNow( url, audio );
		}
		finally
		{
			checkingMp4 = false;
		}
	}

	void PlayFile( string path, bool audio )
	{
		try
		{
			if ( audio )
			{
				music = MusicPlayer.Play( FileSystem.Data, path );
				music.OnFinished += () => Finished = true;
			}
			else
			{
				video = new VideoPlayer();
				videoSink.Attach( video, null, null, "video" );
				video.OnLoaded += () => loaded = true;
				video.OnFinished += () => Finished = true;
				video.Play( FileSystem.Data, path );
			}
		}
		catch ( Exception e )
		{
			Error = e.Message;
			Log.Warning( $"[bimp] Failed to play {path}: {e.Message}" );
		}
	}

	void PlayUrlNow( string url, bool audio )
	{
		try
		{
			if ( audio )
			{
				music = MusicPlayer.PlayUrl( url );
				music.OnFinished += () => Finished = true;
			}
			else
			{
				video = new VideoPlayer();
				videoSink.Attach( video, null, null, "video" );
				video.OnLoaded += () => loaded = true;
				video.OnFinished += () => Finished = true;
				video.Play( url );
			}
		}
		catch ( Exception e )
		{
			Error = e.Message;
			Log.Warning( $"[bimp] Failed to play {url}: {e.Message}" );
		}
	}

	/// <summary>
	/// The client side resolve finished - start the right kind of playback.
	/// </summary>
	void StartNative( StreamSession session )
	{
		var plan = session.Plan;

		if ( plan.Kind == StreamKind.Direct )
		{
			// a progressive file - played straight from the site, seeked natively once loaded
			StartUrl( plan.DirectUrl, audioOnly );
			return;
		}

		if ( plan.Kind == StreamKind.Live )
		{
			liveMaxHeight = TokenHeight( token, liveMaxHeight );
			StartLive( plan.DirectUrl, true );
			return;
		}

		// The engine can only seek forward by decoding every frame in between, while the audio runs on - a 4K video
		// can't decode much faster than it plays, so after a seek the picture crawled behind the sound for seconds. So
		// merged media starts at a keyframe close behind where the synced time will be once the first segment is
		// ready, or at the next one and waits there (see SegmentPlayer.SyncedTime) - it never seeks forward. The start
		// of a video just plays from the beginning, a load time behind.
		var planAhead = plan.Kind is StreamKind.Merge or StreamKind.Hls && !audioOnly && startTime >= FreshStartSeconds;
		var plannedStart = planAhead ? startTime + (float)SinceCreated + firstSegmentSeconds : startTime;
		var maxLag = planAhead ? MaxStartLag : float.MaxValue;
		sinceSegmenter = 0;

		if ( plan.Kind == StreamKind.Hls )
		{
			// a finished HLS playlist: remuxed from the segment at (or just after) the planned start, like merged media
			live = new Resolver.Live.LiveStream( plan.DirectUrl, StreamSessions.NewCacheDirectory(), TokenHeight( token, 720 ), true, plannedStart, maxLag );
			segments = new SegmentPlayer( live.Segmenter );
			return;
		}

		segmenter = new WebmSegmenter( plan.Kind == StreamKind.Merge ? session.Video : null, session.Audio, plannedStart, StreamSessions.NewCacheDirectory(), maxLag, session.SwitchVideo );

		if ( plan.Kind == StreamKind.Audio || audioOnly )
		{
			// one file from the cue point before the start time to the end, through MusicPlayer for the spectrum
			_ = StartAudioFile();
			return;
		}

		segments = new SegmentPlayer( segmenter );
	}

	async Task StartAudioFile()
	{
		preparing = null;
		try
		{
			var segment = await segmenter.GetAsync( 0 );
			if ( disposed || segment is null ) return;
			music = MusicPlayer.Play( FileSystem.Data, segment.Path );
			music.OnFinished += () => Finished = true;
		}
		catch ( Exception e )
		{
			if ( disposed ) return;
			Error = e.Message;
			StreamSessions.Forget( token );
		}
	}

	/// <summary>
	/// Call every frame. Pushes the latest video frame to the texture.
	/// </summary>
	public void Present()
	{
		if ( preparing is { IsCompleted: true } task )
		{
			preparing = null;
			if ( task.IsCompletedSuccessfully )
			{
				try
				{
					StartNative( task.Result );
				}
				catch ( Exception e )
				{
					Error = e.Message;
					StreamSessions.Forget( token );
				}
			}
			else
			{
				var e = task.Exception?.InnerException;
				Error = e is ResolveException ? e.Message : $"Couldn't load the stream: {e?.Message}";
				Log.Warning( $"[bimp] Failed to resolve {token}: {e}" );
				StreamSessions.Forget( token );
			}
		}

		if ( !firstSegmentTimed && ((ISegmentFeed)segmenter ?? (live?.Segmenter is { IsLive: false } vod ? vod : null))?.TryGet( 0 ) is not null )
		{
			firstSegmentTimed = true;
			firstSegmentSeconds = Math.Clamp( firstSegmentSeconds * 0.5f + sinceSegmenter * 0.5f, 0.3f, 10.0f );
		}

		segments?.Update();
		if ( video is not null )
		{
			var presentStart = System.Diagnostics.Stopwatch.GetTimestamp();
			video.Present();
			MediaProbe.PresentTook( System.Diagnostics.Stopwatch.GetElapsedTime( presentStart ).TotalMilliseconds, "direct", 0 );
		}

		if ( live is not null && segments is not null )
		{
			live.PlayingSeq = segments.CurrentSeq;
			// camera G.711 audio plays beside the video, lined up with the picture on screen
			live.Segmenter.Pcm?.Update( segments.StreamTime, segments.Loaded && requestedPaused != true );
		}

		// MusicPlayer has no loaded event, so treat advancing time as loaded
		var t = PlaybackTime;
		if ( t > lastPlaybackTime + 0.01f ) loaded = true;
		lastPlaybackTime = t;
	}

	bool? requestedPaused;
	RealTimeSince sincePauseRequest;

	/// <summary>
	/// Pause or resume. Only calls into the native player when the requested state changes - not by
	/// polling its IsPaused every frame, which can lag a call and made us call Pause/Resume repeatedly
	/// (every extra Resume restarts the decoder's clock, which shows up as stutter).
	/// If the native player still disagrees a while later (e.g. it hadn't started yet), ask once more.
	/// </summary>
	public void SetPaused( bool paused )
	{
		if ( requestedPaused == paused )
		{
			if ( IsPaused == paused || sincePauseRequest < 1.0f ) return;
		}

		requestedPaused = paused;
		sincePauseRequest = 0;
		MediaProbe.Note( paused ? "native Pause()" : "native Resume()" );

		segments?.SetPaused( paused );

		if ( video is not null )
		{
			if ( paused ) video.Pause(); else video.Resume();
		}

		if ( music is not null )
			music.Paused = paused;
	}

	/// <summary>
	/// Seek to a position within the media (not accounting for <see cref="TimeOffset"/> - pass media time).
	/// </summary>
	public void Seek( float mediaTime )
	{
		var local = MathF.Max( 0, mediaTime - TimeOffset );
		MediaProbe.Note( $"native Seek({local:0.000}) from {PlaybackTime:0.000}" );
		segments?.Seek( local );
		video?.Seek( local );
		music?.Seek( local );
		Finished = false;
	}

	public void SetAudio( Vector3 position, bool spatial, float volume, float distance )
	{
		segments?.SetAudio( position, spatial, volume, distance );
		live?.Segmenter.Pcm?.SetAudio( position, spatial, volume, distance );

		// Non-spatial audio is played at the listener, slightly in front so it's centered
		var pos = spatial ? position : Vector3.Forward * 64.0f;

		if ( video is not null )
		{
			var a = video.Audio;
			a.ListenLocal = !spatial;
			a.Position = pos;
			a.Volume = volume;
			a.Distance = distance;
			a.Falloff = MediaPlayer.AudioFalloff;
		}

		if ( music is not null )
		{
			music.ListenLocal = !spatial;
			music.Position = pos;
			music.Volume = volume;
			music.Distance = distance;
			music.Falloff = MediaPlayer.AudioFalloff;
		}
	}

	public void Dispose()
	{
		disposed = true;
		preparing = null;
		cts.Cancel();

		segments?.Dispose();
		segments = null;

		video?.Dispose();
		video = null;

		music?.Dispose();
		music = null;

		videoSink.Dispose();

		// after the players, so the files aren't open anymore
		segmenter?.Dispose();
		segmenter = null;

		live?.Dispose();
		live = null;

		if ( localDirectory is not null )
		{
			try
			{
				if ( FileSystem.Data.DirectoryExists( localDirectory ) ) FileSystem.Data.DeleteDirectory( localDirectory, true );
			}
			catch ( Exception e )
			{
				Log.Trace( $"[bimp] couldn't delete {localDirectory}: {e.Message}" );
			}
			localDirectory = null;
		}
	}
}
