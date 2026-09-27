using System.Threading;
using Bimp.Resolver.Extractors;

namespace Bimp.Resolver;

/// <summary>
/// The scene's shared media resolver. There's exactly one per scene; it does nothing until a
/// <see cref="MediaPlayer"/> asks it for something, and only ever runs on the host - clients send their
/// requests to the host through the player's RPCs, and get the result through its synced state.
/// <para>
/// It works out what a url is (title, duration, qualities, dubs) and turns it into a queue item whose play url
/// is a <see cref="PlayToken"/>. It never hands out stream urls: those are tied to the IP that asked for them,
/// so each client resolves its own (see <see cref="StreamSessions"/>).
/// </para>
/// </summary>
public sealed class BimpResolverSystem : GameObjectSystem<BimpResolverSystem>
{
	const float InfoLifetime = 60 * 30;
	const int MaxCached = 500;

	class Cached
	{
		public Task<MediaInfo> Task;
		public RealTimeSince Created;
	}

	/// <summary> Results (and requests in flight, so two people queueing the same link resolve it once). </summary>
	readonly Dictionary<string, Cached> cache = new();

	public BimpResolverSystem( Scene scene ) : base( scene )
	{
	}

	/// <summary>
	/// Resolve what the user typed into a playable queue item. Host only.
	/// Throws <see cref="ResolveException"/> with a user facing message on failure.
	/// </summary>
	public async Task<MediaQueueItem> ResolveAsync( string input, bool audioOnly )
	{
		if ( Networking.IsActive && !Networking.IsHost )
			throw new InvalidOperationException( "Only the host resolves media." );

		if ( !MediaSource.TryNormalize( input, out var uri ) )
			throw new ResolveException( "That doesn't look like a valid url." );

		// Live streams: every client connects on its own (see Live.LiveStream). HLS playlists are looked at first -
		// a finished one is a seekable video (see HlsExtractor).
		if ( Live.LiveStream.IsLiveUrl( uri ) && !Extractors.HlsExtractor.IsPlaylist( uri ) )
			return MediaSource.LiveItem( uri, audioOnly );

		// Files the engine plays directly
		if ( MediaSource.IsDirectMedia( uri ) )
			return MediaSource.DirectItem( uri, audioOnly );

		var extractor = ExtractorRegistry.For( uri );
		if ( extractor is null )
		{
			// Might still be a direct stream without a file extension (internet radio etc)
			return MediaSource.DirectItem( uri, audioOnly );
		}

		var info = await GetInfoAsync( extractor, uri, audioOnly );
		var audio = audioOnly || info.AudioOnly;
		var maxh = Math.Clamp( MediaSettings.MaxVideoHeight, 144, 2160 );

		return new MediaQueueItem
		{
			Url = uri.ToString(),
			PlayUrl = PlayToken.Create( info.Extractor, info.Id, audio, maxh ),
			Title = string.IsNullOrWhiteSpace( info.Title ) ? uri.ToString() : info.Title,
			Duration = info.IsLive ? 0 : Math.Max( 0, info.Duration ),
			IsLive = info.IsLive,
			AudioOnly = audio,
			// merged media plays from local segment files starting at a time - it seeks by reopening
			SeekByReload = info.Merged && !info.IsLive,
			Qualities = audio ? null : MediaStreamOptions.EncodeQualities( info.Qualities ),
			AudioTracks = MediaStreamOptions.EncodeAudioTracks( info.AudioTracks ),
		};
	}

	Task<MediaInfo> GetInfoAsync( IExtractor extractor, Uri uri, bool audioOnly )
	{
		var key = $"{extractor.Key}|{(audioOnly ? "a" : "v")}|{uri}";

		if ( cache.TryGetValue( key, out var c ) && c.Created < InfoLifetime && !c.Task.IsFaulted && !c.Task.IsCanceled )
			return c.Task;

		if ( cache.Count >= MaxCached )
		{
			foreach ( var old in cache.Where( kv => kv.Value.Created > InfoLifetime || kv.Value.Task.IsFaulted ).Select( kv => kv.Key ).ToList() )
				cache.Remove( old );
			if ( cache.Count >= MaxCached ) cache.Clear();
		}

		Log.Info( $"[bimp] resolving {uri} ({extractor.Key})" );
		var task = Wrap( extractor.GetInfoAsync( uri, audioOnly, CancellationToken.None ) );
		cache[key] = new Cached { Task = task, Created = 0 };
		return task;
	}

	static async Task<MediaInfo> Wrap( Task<MediaInfo> task )
	{
		try
		{
			return await task;
		}
		catch ( ResolveException )
		{
			throw;
		}
		catch ( Exception e )
		{
			// network errors and the like
			throw new ResolveException( $"Couldn't resolve that link: {e.Message}" );
		}
	}
}
