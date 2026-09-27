namespace Bimp;

/// <summary>
/// Takes decoded video frames from <see cref="VideoPlayer"/>s (through <see cref="VideoPlayer.OnTextureData"/>) and
/// uploads them to one texture we own, which is what the screen draws.
/// <para>
/// ROOT CAUSE of the constant stutter: the engine's own path - VideoPlayer rendering into its Texture - shows frames
/// unevenly. Measured by reading the texture back every frame: 1-4 frames a second were held for 67 ms+ (and
/// others shown early), with any source - 720p or 1080p, with or without B-frames or audio, one player or
/// segments - while the same players delivered every frame on time through OnTextureData (30 fps, p95 34 ms).
/// So the decode is fine and the jitter is added between the decoder and the engine's texture. Uploading the
/// delivered frames ourselves bypasses it.
/// </para>
/// </summary>
public sealed class VideoFrameSink : IDisposable
{
	Texture texture;

	/// <summary> The latest frame. Null until the first one arrives. </summary>
	public Texture Texture => texture;

	public int Width { get; private set; }
	public int Height { get; private set; }

	/// <summary> Frames uploaded so far. </summary>
	public int Frames { get; private set; }

	/// <summary>
	/// The latest frame a player delivered while it wasn't on screen - a preloading segment player's first frame
	/// usually arrives just before the swap, and dropping it left the old picture up one frame too long.
	/// </summary>
	sealed class Held
	{
		public byte[] Data;
		public int Width, Height;
		public bool Valid;
	}

	readonly Dictionary<VideoPlayer, Held> held = new();

	/// <summary>
	/// One buffer for held frames, reused (only one player preloads at a time). A fresh 8 MB array per segment
	/// landed on the large object heap every swap - food for the periodic full GCs that stall the game.
	/// </summary>
	byte[] heldBuffer;

	/// <summary>
	/// Route a player's frames here. <paramref name="isActive"/> says whether this player is the one on screen
	/// (a preloading segment player isn't); <paramref name="onFrame"/> runs for every frame it delivers either way.
	/// Set <paramref name="holdWhileInactive"/> for a player that's about to take over, so its latest frame can be
	/// shown the moment it does (see <see cref="ShowHeld"/>).
	/// </summary>
	public void Attach( VideoPlayer player, Func<bool> isActive, Action onFrame = null, string who = null, bool holdWhileInactive = false )
	{
		Held keep = holdWhileInactive ? held[player] = new Held() : null;
		var leadingSkipped = 0;
		var gotPicture = false;

		player.OnTextureData = ( span, size ) =>
		{
			int w = (int)size.x, h = (int)size.y;

			// A freshly opened player's first output is an all-black frame (measured: pure black, at every segment
			// swap - a black flash each time). Skip black frames until its first real picture, capped so a stream
			// that genuinely starts on black still shows.
			if ( !gotPicture )
			{
				if ( leadingSkipped < MaxLeadingBlackFrames && IsBlack( span, w, h ) )
				{
					leadingSkipped++;
					return;
				}
				gotPicture = true;
			}

			onFrame?.Invoke();
			MediaProbe.CountFrame( who );

			if ( isActive is null || isActive() )
			{
				Upload( span, w, h );
				if ( keep is not null ) keep.Valid = false;
				return;
			}

			if ( keep is null || span.Length < w * h * 4 ) return;
			if ( heldBuffer is null || heldBuffer.Length != w * h * 4 ) heldBuffer = new byte[w * h * 4];
			keep.Data = heldBuffer;
			span[..(w * h * 4)].CopyTo( keep.Data );
			keep.Width = w;
			keep.Height = h;
			keep.Valid = true;
		};
	}

	/// <summary> Leading black frames skipped at most, per player (~0.25 s at 30 fps). </summary>
	const int MaxLeadingBlackFrames = 8;

	/// <summary> Near-black, judged from a sparse grid of ~1000 pixels. </summary>
	static bool IsBlack( ReadOnlySpan<byte> rgba, int width, int height )
	{
		if ( width <= 0 || height <= 0 || rgba.Length < width * height * 4 ) return false;
		long sum = 0;
		int count = 0;
		var stepY = Math.Max( 1, height / 32 );
		var stepX = Math.Max( 1, width / 32 );
		for ( int y = stepY / 2; y < height; y += stepY )
		{
			var row = y * width * 4;
			for ( int x = stepX / 2; x < width; x += stepX )
			{
				var i = row + x * 4;
				var v = rgba[i] + rgba[i + 1] + rgba[i + 2];
				if ( v > 3 * 24 ) return false; // anything clearly lit means it's a picture
				sum += v;
				count++;
			}
		}
		return count > 0 && sum / (3.0 * count) < 4;
	}

	/// <summary> A player just took over the screen: show the frame it delivered while waiting, if any. </summary>
	public void ShowHeld( VideoPlayer player )
	{
		if ( player is null || !held.Remove( player, out var keep ) || !keep.Valid ) return;
		Upload( keep.Data, keep.Width, keep.Height );
	}

	/// <summary> A player is going away. </summary>
	public void Detach( VideoPlayer player )
	{
		if ( player is not null ) held.Remove( player );
	}

	/// <summary> A decoded image (Motion JPEG) straight to the screen texture. </summary>
	public void Upload( Bitmap bitmap )
	{
		if ( bitmap is null || !bitmap.IsValid ) return;
		EnsureTexture( bitmap.Width, bitmap.Height );
		if ( !MediaProbe.NoUpload ) texture.Update( bitmap );
		Frames++;
		MediaProbe.DeliveredFrame();
	}

	void EnsureTexture( int width, int height )
	{
		if ( texture is not null && width == Width && height == Height ) return;
		texture?.Dispose();
		texture = Texture.Create( width, height, ImageFormat.RGBA8888 )
			.WithName( "bimp-video" )
			.WithDynamicUsage()
			.Finish();
		Width = width;
		Height = height;
	}

	void Upload( ReadOnlySpan<byte> data, int width, int height )
	{
		if ( width <= 0 || height <= 0 || data.Length < width * height * 4 ) return;

		if ( texture is null || width != Width || height != Height )
		{
			texture?.Dispose();
			texture = Texture.Create( width, height, ImageFormat.RGBA8888 )
				.WithName( "bimp-video" )
				.WithDynamicUsage()
				.Finish();
			Width = width;
			Height = height;
		}

		if ( !MediaProbe.NoUpload ) texture.Update( data );
		Frames++;
		MediaProbe.DeliveredFrame( data, width, height );
	}

	public void Dispose()
	{
		held.Clear();
		texture?.Dispose();
		texture = null;
	}
}
