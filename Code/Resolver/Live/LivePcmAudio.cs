namespace Bimp.Resolver.Live;

/// <summary>
/// G.711 (PCMU / PCMA) decoding - the audio most IP cameras send, which MP4 can't carry.
/// </summary>
public static class G711
{
	static readonly short[] MuLaw = new short[256];
	static readonly short[] ALaw = new short[256];

	static G711()
	{
		for ( int i = 0; i < 256; i++ )
		{
			// ITU-T G.711 mu-law
			var u = ~i & 0xFF;
			var t = ((u & 0x0F) << 3) + 0x84;
			t <<= (u & 0x70) >> 4;
			MuLaw[i] = (short)((u & 0x80) != 0 ? 0x84 - t : t - 0x84);

			// A-law
			var a = i ^ 0x55;
			var seg = (a & 0x70) >> 4;
			var v = (a & 0x0F) << 4;
			v = seg switch { 0 => v + 8, 1 => v + 0x108, _ => (v + 0x108) << (seg - 1) };
			ALaw[i] = (short)((a & 0x80) != 0 ? v : -v);
		}
	}

	public static short[] Decode( ReadOnlySpan<byte> data, bool alaw )
	{
		var table = alaw ? ALaw : MuLaw;
		var result = new short[data.Length];
		for ( int i = 0; i < data.Length; i++ ) result[i] = table[data[i]];
		return result;
	}
}

/// <summary>
/// Plays decoded PCM audio (G.711 from a camera, or AAC in low latency mode) through a <see cref="SoundStream"/>,
/// delayed so it lines up with the video: samples are written when the picture on screen reaches their stream
/// time, not when they arrive (the video runs behind, since it's cut into segments). Samples are interleaved.
/// </summary>
public sealed class LivePcmAudio : IDisposable
{
	readonly int sampleRate;
	readonly int channels;
	readonly Queue<(double time, short[] samples)> queue = new();
	SoundStream stream;
	SoundHandle handle;
	double writtenUntil = double.NegativeInfinity;

	// last audio settings, applied when the sound starts
	Vector3 position;
	bool spatial;
	float volume = 1;
	float distance = 2500;

	public int SampleRate => sampleRate;

	public int Channels => channels;

	/// <summary>
	/// Stream time of the audio being heard minus that of the picture on screen (seconds): negative means the audio
	/// is late. What's queued in the stream and the mixer's own latency are counted as not yet heard.
	/// </summary>
	public double? Offset { get; private set; }

	/// <summary>
	/// <see cref="SoundStream.QueuedSampleCount"/> and <see cref="SoundStream.LatencySamplesCount"/> count the mixer's
	/// samples, not ours (measured: 8 kHz audio queued ~5.5 counts per sample written). Taking them as ours made the
	/// queue look 5.5x longer than it was, so audio piled up seconds deep and played ~0.3 s late, drifting.
	/// </summary>
	const double MixerRate = 44100;

	/// <summary> Most audio (seconds) handed to the stream ahead of time. </summary>
	const double MaxQueued = 0.3;

	/// <summary> Diagnostics: seconds queued in the stream, written ahead of the picture, and packets waiting. </summary>
	public string State => stream is null ? "not started" : $"queued {(stream.QueuedSampleCount + stream.LatencySamplesCount) / MixerRate:0.000}s, written {writtenUntil - lastVideoTime:+0.000;-0.000}s past the picture, {queue.Count} waiting, newest {(newest - lastVideoTime):+0.000;-0.000}s";
	double lastVideoTime, newest = double.NegativeInfinity;

	public LivePcmAudio( int sampleRate, int channels = 1 )
	{
		this.sampleRate = sampleRate;
		this.channels = Math.Max( 1, channels );
	}

	/// <summary> Samples starting at this stream time (seconds). </summary>
	public void Add( double time, short[] samples )
	{
		newest = Math.Max( newest, time );
		queue.Enqueue( (time, samples) );
		while ( queue.Count > 2000 ) queue.Dequeue(); // nothing's playing it - don't grow forever
	}

	/// <summary>
	/// Call every frame with the stream time of the picture on screen.
	/// </summary>
	public void Update( double videoTime, bool playing )
	{
		if ( !playing ) return;
		lastVideoTime = videoTime;

		if ( stream is null )
		{
			stream = new SoundStream( sampleRate, channels );
			handle = stream.Play( volume );
			Apply();
		}

		// what the stream still has queued plays before anything we write now: a packet written now is heard
		// when the picture is that much further on
		var queued = (stream.QueuedSampleCount + stream.LatencySamplesCount) / MixerRate;

		while ( queue.Count > 0 )
		{
			var (time, samples) = queue.Peek();
			var length = samples.Length / channels / (double)sampleRate;
			var heardAt = videoTime + queued;

			// late: it would be heard after the picture has passed it (a hold or jump in the picture) - skip it
			if ( time + length < heardAt - 0.02 ) { queue.Dequeue(); continue; }

			// not yet: it would play ahead of the picture
			if ( time > heardAt + 0.03 ) break;

			// never queue much in the stream: whatever's queued there plays even if the picture holds or jumps
			if ( queued > MaxQueued ) break;

			queue.Dequeue();
			stream.WriteData( samples );
			queued += length;
			writtenUntil = time + length;
		}

		if ( writtenUntil > double.NegativeInfinity )
			Offset = writtenUntil - (stream.QueuedSampleCount + stream.LatencySamplesCount) / MixerRate - videoTime;
	}

	public void SetAudio( Vector3 pos, bool isSpatial, float vol, float dist )
	{
		position = pos;
		spatial = isSpatial;
		volume = vol;
		distance = dist;
		Apply();
	}

	void Apply()
	{
		if ( !handle.IsValid() ) return;
		handle.ListenLocal = !spatial;
		handle.Position = spatial ? position : Vector3.Forward * 64.0f;
		handle.Volume = volume;
		handle.Distance = distance;
		handle.Falloff = MediaPlayer.AudioFalloff;
	}

	public void Dispose()
	{
		if ( handle.IsValid() ) handle.Stop();
		stream?.Dispose();
		stream = null;
		queue.Clear();
	}
}
