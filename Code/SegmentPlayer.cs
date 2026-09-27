using Bimp.Resolver.Media;

namespace Bimp;

/// <summary>
/// Plays a series of local segment files back to back: merged YouTube media (<see cref="WebmSegmenter"/>) or a
/// live stream remuxed to MP4 (<see cref="Resolver.Live.LiveSegmenter"/>).
/// <para>
/// Segment N plays while N+1 is started just in time in a second, hidden and silent player; at the boundary
/// they swap, so the picture carries straight on. Segments keep absolute timestamps, so <see cref="Time"/>
/// is the media time.
/// </para>
/// </summary>
public sealed class SegmentPlayer : IDisposable
{
	class Slot
	{
		public MediaSegment Segment;
		public VideoPlayer Player;
		public bool Loaded;
		public bool Finished;
		public bool Preload;    // waiting in the wings: silent until it's swapped in
		public bool FirstFrame; // set when it delivers its first frame (from OnTextureData, during Present)
		public bool Primed;     // its decoder has produced its first frame
		public int Steady;      // frames in a row it has delivered on time (see SettleFrames)
		public RealTimeSince SinceFrame;
		public bool Fading;     // swapped out: silent (its volume ramps down) and closed a moment later
		public float LastTime;
		public RealTimeSince SinceOpened;
	}

	readonly ISegmentFeed feed;

	/// <summary>
	/// Live: start this many segments behind the newest finished one, so the next is always ready by the time the
	/// current one ends. Latency is roughly (JoinBehind + 1) segments plus the overlap.
	/// </summary>
	int JoinBehind => feed.LowLatency ? 0 : 1;

	/// <summary> Live: further behind than this (slow machine, stalled download) jumps back to the live edge. </summary>
	int MaxBehind => feed.LowLatency ? 1 : 3;

	/// <summary>
	/// Low latency: join the newest segment this long after it's written, so the next one is ready a little before
	/// the current one ends (its start latency plus arrival jitter). A segment that's late anyway just holds the
	/// last picture (the filler frames) until it arrives.
	/// </summary>
	const double LowJoinMargin = 0.2;

	/// <summary>
	/// Average time from opening a segment to its first decoded frame (measured as we go).
	/// </summary>
	float startLatency;

	/// <summary>
	/// How long before the current segment ends to start the next one, so its first frame lands on the boundary -
	/// and so the two players are close to in step for the audio crossfade. A player that isn't primed in time
	/// just swaps a moment later (the current one plays on into its overlap, if the file has one).
	/// </summary>
	float StartLead => JustInTime
		? Math.Clamp( (startLatency > 0 ? startLatency : 0.08f) + 0.015f, 0.0f, 1.0f )
		: ClockLeadOnly ? Math.Clamp( clockLead, 0.0f, Math.Min( current.Segment.Overlap, 1.0f ) )
		: Math.Clamp( (startLatency > 0 ? startLatency : 0.04f) + leadCorrection, 0.0f, feed.IsLive ? 1.0f : 3.0f );

	/// <summary>
	/// Media files that run on into the next segment (<see cref="MediaSegment.Overlap"/>): the current player shows real
	/// frames until the next one's first frame is up, however long that takes (150-520 ms at 1440p/4K), so the next one
	/// only has to be opened for its clock to be in step. Adding the start latency to that made it skip that far ahead
	/// at a swap, and correcting that back made the current one hold its last frame instead.
	/// <para>
	/// Merged WebM (VP9/AV1) only: MP4 segments (HLS VOD) kept waiting for the next H.264 player to settle made Media
	/// Foundation drop a sample ("MFT deadlock") in every run, and the picture broke up until the next keyframe (3 of 3
	/// runs, against 0 of 3 with the old timing).
	/// </para>
	/// </summary>
	bool ClockLeadOnly => feed is WebmSegmenter && (current?.Segment.Overlap ?? 0) > 0;

	/// <summary> How long before the end to open the next player so its clock is in step (<see cref="ClockLeadOnly"/>), learned from each swap. </summary>
	float clockLead = 0.05f;

	/// <summary>
	/// <see cref="ClockLeadOnly"/>: swap once the next player has delivered this many frames in a row on time. Its first
	/// frame comes late, and a 1440p decoder is still catching up to its clock for a moment after it - swapping on the
	/// first frame put that on screen as a 80-120 ms gap (measured). The current one plays on into its overlap meanwhile.
	/// </summary>
	const int SettleFrames = 4;
	const float SettleInterval = 0.045f;

	/// <summary>
	/// Low latency files that end at their boundary (H.264, ending in skip frames): the next one just opens early enough
	/// for its first frame to be ready there. (Syncing clocks made every swap ~40 ms late: a new player's clock runs from
	/// Play(), its first frame comes ~60 ms later.)
	/// </summary>
	bool JustInTime => feed.LowLatency && (current?.Segment.Overlap ?? 0) <= 0;

	/// <summary>
	/// Learned from each swap: how far the next player's clock was ahead of the current one's at the boundary.
	/// Starting it that much later next time brings them in step, which the audio crossfade needs - and so the next
	/// player neither skips its first frames nor makes the current one hold its last, while the current one plays on
	/// into its overlap (see <see cref="MediaSegment.Overlap"/>).
	/// </summary>
	float leadCorrection;

	/// <summary>
	/// How long the swapped-out player keeps running, silent, after a swap.
	/// <para>
	/// The engine's mixer runs every sound through Steam Audio's direct effect, whose gain interpolator ramps any
	/// volume change over the next mix frames. Unmuting the next player while closing the current one outright
	/// left a ~70 ms dip in the audio at every boundary (measured with a sine: down to 5%, every segment).
	/// Muting the old player at the same moment instead - both ramps at once - makes that a crossfade.
	/// </para>
	/// </summary>
	const float FadeSeconds = 0.15f;

	Slot fading;
	RealTimeSince sinceFade;

	Slot current, next;
	bool paused;

	// audio settings, applied to both players
	Vector3 audioPosition;
	bool audioSpatial;
	float audioVolume = 1;
	float audioDistance = 2500;

	string error;
	public string Error => error ?? feed.Error;

	/// <summary> The segment on screen, -1 before the first. </summary>
	public int CurrentSeq => current?.Segment.Seq ?? -1;

	/// <summary> Has the first segment started? </summary>
	public bool Loaded => current is { Loaded: true } || (Mjpeg?.HasShown ?? false);

	/// <summary> Motion JPEG: frames go straight to the texture, there are no segments to play. </summary>
	Resolver.Live.LiveMjpeg Mjpeg => (feed as Resolver.Live.LiveSegmenter)?.Mjpeg;

	/// <summary> The last segment has played to its end. </summary>
	public bool Finished { get; private set; }

	/// <summary> Media time (VOD), or running stream time (live). </summary>
	public float Time => (float)StreamTime;

	/// <summary>
	/// Stream time (seconds) of what's on screen. WebM segments keep absolute timestamps (their StreamStart is 0); MP4
	/// segments (live, HLS VOD) each start their clock at 0.
	/// </summary>
	public double StreamTime => Mjpeg is { } mjpeg ? mjpeg.ShownTime : (current?.Segment.StreamStart ?? 0) + (current?.Player?.PlaybackTime ?? 0);

	public float Duration => current?.Player?.Duration ?? 0;
	public bool IsPaused => current?.Player?.IsPaused ?? paused;

	/// <summary> Every segment's frames go to one texture (see <see cref="VideoFrameSink"/>). </summary>
	readonly VideoFrameSink sink = new();

	public Texture Texture => sink.Texture;
	public int Width => sink.Width;
	public int Height => sink.Height;
	public bool HasVideo => sink.Texture is not null && Width > 4 && Height > 4;
	public (int rate, int channels) AudioFormat => (current?.Player?.SampleRate ?? 0, current?.Player?.Channels ?? 0);

	/// <summary>
	/// Merged media: the synced media time, null while paused. A first segment that starts later than it (at the next
	/// keyframe - see <see cref="MediaBackend"/>) opens only when the clock gets there, so it never has to seek forward.
	/// </summary>
	public float? SyncedTime { get; set; }

	public SegmentPlayer( ISegmentFeed feed )
	{
		this.feed = feed;
		if ( !feed.IsLive ) feed.Want( 0 );
	}

	/// <summary>
	/// Call every frame.
	/// </summary>
	public void Update()
	{
		if ( Mjpeg is { } mjpeg )
		{
			if ( !paused ) mjpeg.Present( sink );
			return;
		}

		if ( current is null )
		{
			var first = feed.IsLive ? LiveJoinSegment() : feed.TryGet( 0 );
			if ( first is null ) return;
			if ( !feed.IsLive && !paused && SyncedTime is { } now && first.StreamStart + first.Start - now > StartLead ) return;
			current = Open( first );
			if ( paused ) current.Player?.Pause();
		}

		// Live and fell far behind? Jump to the live edge.
		if ( feed.IsLive && feed.Newest - current.Segment.Seq > MaxBehind && LiveJoinSegment() is { } edge )
		{
			Log.Info( $"[bimp] live: {feed.Newest - current.Segment.Seq} segments behind, jumping to the live edge" );
			Close( next ); next = null;
			Close( current );
			current = Open( edge );
		}

		TimedPresent( current, "current" );

		// the first segment's own start seeds the start latency, so the first swap isn't late on a slow (4K) decoder
		if ( startLatency <= 0 && !current.Primed && current.FirstFrame )
		{
			current.Primed = true;
			startLatency = (float)current.SinceOpened;
		}

		// the swapped-out player finishes its fade, then goes
		if ( fading is not null )
		{
			TimedPresent( fading, "fading" );
			if ( sinceFade > FadeSeconds ) CloseFading();
		}

		var p = current.Player;
		var seg = current.Segment;

		// A player started fresh delivers its first frame ~0.25s after Play(), and one that was paused takes ~1s
		// after Resume() - so start the next segment just in time (hidden, silent, presented so it runs),
		// timed from the measured start latency, and swap which player is on screen. Nothing ever pauses.
		if ( next is null && !paused && !seg.Last && current.Loaded && p is not null && p.PlaybackTime >= seg.End - StartLead )
		{
			var upcoming = feed.TryGet( seg.Seq + 1 );
			if ( upcoming is not null ) next = Open( upcoming, preload: true );
		}

		if ( next?.Player is not null )
		{
			TimedPresent( next, "next" );

			if ( !next.Primed && next.FirstFrame )
			{
				next.Primed = true;
				var latency = (float)next.SinceOpened;
				startLatency = startLatency <= 0 ? latency : startLatency * 0.7f + latency * 0.3f;
				MediaProbe.Note( $"seg {next.Segment.Seq} first frame {latency * 1000:0}ms after opening (avg {startLatency * 1000:0}ms), current at {p?.PlaybackTime:0.000}/{seg.End:0.000}" );
			}
		}

		// Swap when the current segment's clock reaches its end. (OnFinished fires ~80ms early, while
		// frames are still queued - swapping on it cut off the last frames.)
		var stuck = current.Finished && p is not null && MathF.Abs( p.PlaybackTime - current.LastTime ) < 0.0001f;
		current.LastTime = p?.PlaybackTime ?? 0;
		var atEnd = (current.Loaded && p is not null && p.PlaybackTime >= seg.End - 0.005f) || stuck;
		if ( !atEnd ) return;

		if ( seg.Last )
		{
			Finished = current.Finished || stuck;
			return;
		}

		// a next player still catching up waits, while the current one has overlap left to play
		var settled = !ClockLeadOnly || next is null || next.Steady >= SettleFrames || (p is not null && p.PlaybackTime >= seg.End + seg.Overlap - 0.1f) || stuck;
		if ( next is { Primed: true } && settled )
		{
			// the next player's clock vs the current one's, in stream time: + means it's ahead. (Merged media keeps
			// absolute timestamps; live segments each start their clock at 0.)
			var ahead = (float)((next.Segment.StreamStart + (next.Player?.PlaybackTime ?? 0)) - (seg.StreamStart + (p?.PlaybackTime ?? seg.End)));

			// keep the two clocks in step (see leadCorrection)
			if ( ClockLeadOnly ) clockLead = Math.Clamp( clockLead - ahead * 0.7f, 0.0f, 1.0f );
			else if ( !JustInTime ) leadCorrection = Math.Clamp( leadCorrection - ahead * 0.7f, -0.2f, 0.2f );
			MediaProbe.Note( $"swap {seg.Seq}->{next.Segment.Seq} at {p?.PlaybackTime:0.000}/{seg.End:0.000} (overlap {seg.Overlap * 1000:0}ms), next ahead by {ahead * 1000:0}ms, lead now {StartLead * 1000:0}ms" );
			next.Preload = false;
			ApplyAudio( next );

			// its frame from just before the boundary goes up now, not 33 ms later with its next one
			sink.ShowHeld( next.Player );

			// crossfade: the old player goes silent as the new one comes up (see FadeSeconds)
			CloseFading();
			fading = current;
			fading.Fading = true;
			ApplyAudio( fading );
			sinceFade = 0;

			current = next;
			next = null;
			feed.Want( current.Segment.Seq ); // keeps the one after it coming
		}

		// otherwise the next segment isn't downloaded yet - hold the last frame
	}

	void TimedPresent( Slot slot, string role )
	{
		if ( slot?.Player is null ) return;
		var t = System.Diagnostics.Stopwatch.GetTimestamp();
		slot.Player.Present();
		MediaProbe.PresentTook( System.Diagnostics.Stopwatch.GetElapsedTime( t ).TotalMilliseconds, role, slot.Segment.Seq );
	}

	/// <summary>
	/// Live: the segment to start from - the newest one that's at least <see cref="JoinBehind"/> behind the newest
	/// finished segment and leaves the feed's start buffer of stream time after it. Null while there isn't enough yet.
	/// </summary>
	MediaSegment LiveJoinSegment()
	{
		var newest = feed.TryGet( feed.Newest );
		if ( newest is null ) return null;
		var bufferedUntil = newest.StreamStart + newest.End;

		if ( feed.LowLatency ) return RealTime.NowDouble - newest.ReadyAt >= LowJoinMargin ? newest : null;

		for ( var seq = newest.Seq - JoinBehind; seq >= 0; seq-- )
		{
			var s = feed.TryGet( seq );
			if ( s is null ) return null; // not enough on disk yet
			if ( bufferedUntil - s.StreamStart >= feed.StartBufferSeconds ) return s;
		}
		return null;
	}

	Slot Open( MediaSegment segment, bool preload = false )
	{
		MediaProbe.Note( $"open seg {segment.Seq} ({segment.Start:0.00}-{segment.End:0.00}){(preload ? " (preload)" : "")}" );
		var slot = new Slot { Segment = segment, SinceOpened = 0, Preload = preload };
		try
		{
			var player = new VideoPlayer();
			player.OnLoaded += () => slot.Loaded = true;
			player.OnFinished += () => slot.Finished = true;
			slot.Player = player;
			ApplyAudio( slot );
			// only the player on screen updates the texture; a preloading one just says it's ready
			sink.Attach( player, () => !slot.Preload && !slot.Fading, () =>
			{
				slot.Steady = slot.FirstFrame && slot.SinceFrame < SettleInterval ? slot.Steady + 1 : 0;
				slot.SinceFrame = 0;
				slot.FirstFrame = true;
			}, $"seg {segment.Seq}", holdWhileInactive: preload );
			var playTimer = System.Diagnostics.Stopwatch.StartNew();
			player.Play( FileSystem.Data, segment.Path );
			MediaProbe.Note( $"Play() seg {segment.Seq} took {playTimer.Elapsed.TotalMilliseconds:0.0}ms" );
		}
		catch ( Exception e )
		{
			error = e.Message;
		}
		return slot;
	}

	void Close( Slot slot )
	{
		if ( slot?.Player is null ) return;
		var timer = System.Diagnostics.Stopwatch.StartNew();
		sink.Detach( slot.Player );
		slot.Player.Dispose();
		MediaProbe.Note( $"close seg {slot.Segment.Seq} took {timer.Elapsed.TotalMilliseconds:0.0}ms" );
	}

	void CloseFading()
	{
		if ( fading is null ) return;
		Close( fading );
		feed.Release( fading.Segment.Seq );
		fading = null;
	}

	public void SetPaused( bool pause )
	{
		paused = pause;
		if ( current?.Player is { } p )
		{
			if ( pause ) p.Pause(); else p.Resume();
		}

		if ( pause ) CloseFading();

		// a preloading player would carry on running - start it again when we resume
		if ( pause && next is not null )
		{
			Close( next );
			next = null;
		}
	}

	/// <summary>
	/// A short hop within the current segment (landing exactly on the synced time after opening).
	/// Anything further is done by opening a new segmenter at the time.
	/// </summary>
	public void Seek( float time )
	{
		var seg = current?.Segment;
		if ( seg is null ) return;
		current.Player?.Seek( Math.Clamp( time - (float)seg.StreamStart, seg.Start, seg.End ) );
		Finished = false;
	}

	public void SetAudio( Vector3 position, bool spatial, float volume, float distance )
	{
		audioPosition = position;
		audioSpatial = spatial;
		audioVolume = volume;
		audioDistance = distance;
		ApplyAudio( current );
		ApplyAudio( next );
	}

	void ApplyAudio( Slot slot )
	{
		if ( slot?.Player is null ) return;
		var a = slot.Player.Audio;
		a.ListenLocal = !audioSpatial;
		a.Position = audioSpatial ? audioPosition : Vector3.Forward * 64.0f;
		a.Volume = slot.Preload || slot.Fading ? 0 : audioVolume;
		a.Distance = audioDistance;
	}

	public void Dispose()
	{
		CloseFading();
		Close( current );
		Close( next );
		current = next = null;
		sink.Dispose();
	}
}
