using System.Globalization;
using System.Threading;
using Bimp.Resolver.Extractors;
using Bimp.Resolver.Media;

namespace Bimp.Resolver;

/// <summary>
/// What the host syncs as the play url for natively resolved media: <c>bimp:yt/VIDEOID?mode=av&amp;maxh=720</c>.
/// It names the media, not a stream url - every client resolves its own (stream urls are tied to the IP that
/// asked for them). Clients append their own <c>&amp;h=</c> / <c>&amp;lang=</c> choices.
/// </summary>
public sealed class PlayToken
{
	public const string Prefix = "bimp:";

	public string Extractor { get; init; }
	public string Id { get; init; }
	public bool AudioOnly { get; init; }
	public int MaxHeight { get; init; } = 720;
	public int Height { get; init; }
	public string Language { get; init; }

	public static bool IsToken( string url ) => url is not null && url.StartsWith( Prefix, StringComparison.Ordinal );

	public static string Create( string extractor, string id, bool audioOnly, int maxHeight )
		=> $"{Prefix}{extractor}/{Uri.EscapeDataString( id )}?mode={(audioOnly ? "audio" : "av")}&maxh={maxHeight}";

	/// <summary>
	/// The same token asking for the audio only stream (the view switch, see <see cref="MediaPlayer.SetAudioView"/>).
	/// A token that already is audio only, or isn't one of ours, comes back as it was.
	/// </summary>
	public static string AsAudio( string token ) => IsToken( token ) ? token.Replace( "?mode=av&", "?mode=audio&" ) : token;

	public static PlayToken Parse( string token )
	{
		if ( !IsToken( token ) ) return null;

		var body = token[Prefix.Length..];
		var q = body.IndexOf( '?' );
		var path = q >= 0 ? body[..q] : body;
		var query = System.Web.HttpUtility.ParseQueryString( q >= 0 ? body[(q + 1)..] : "" );

		var slash = path.IndexOf( '/' );
		if ( slash <= 0 ) return null;

		return new PlayToken
		{
			Extractor = path[..slash],
			Id = Uri.UnescapeDataString( path[(slash + 1)..] ),
			AudioOnly = query["mode"] == "audio",
			MaxHeight = int.TryParse( query["maxh"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m ) ? m : 720,
			Height = int.TryParse( query["h"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h ) ? h : 0,
			Language = string.IsNullOrWhiteSpace( query["lang"] ) ? null : query["lang"],
		};
	}

	public StreamRequest ToRequest() => new() { AudioOnly = AudioOnly, MaxHeight = MaxHeight, Height = Height, Language = Language };
}

/// <summary>
/// A client's resolved stream for one play token: the plan, and the probed sources for merged media.
/// </summary>
public sealed class StreamSession
{
	public StreamPlan Plan { get; init; }
	public WebmSource Video { get; init; }
	public WebmSource Audio { get; init; }

	/// <summary> See <see cref="StreamPlan.SwitchVideo"/>. </summary>
	public WebmSource SwitchVideo { get; init; }

	/// <summary> When the first of this session's urls expires (unix seconds, from their <c>expire=</c>), or null if none says. </summary>
	public long? ExpiresAt
	{
		get
		{
			var urls = Plan?.Kind == StreamKind.Direct
				? new[] { RemoteFile.ExpiryOf( Plan.DirectUrl ) }
				: new[] { Audio?.File.ExpiresAt, Video?.File.ExpiresAt, SwitchVideo?.File.ExpiresAt };
			return urls.Where( t => t.HasValue ).Min();
		}
	}
}

/// <summary>
/// Client side stream resolving, cached per token for a while - seeks and drift corrections open a new
/// backend at a new time, and shouldn't have to ask YouTube and re-read the headers every time.
/// </summary>
public static class StreamSessions
{
	const float Lifetime = 60 * 10;

	class Entry
	{
		public Task<StreamSession> Task;
		public RealTimeSince LastUsed;
		public RealTimeSince Created;
	}

	/// <summary> A cached session whose urls expire within this is resolved again (a backend opened now plays for a while on them). </summary>
	const long ReuseExpiryMargin = RemoteFile.ExpiryMargin * 2;

	/// <summary> A session this young isn't judged by its urls' expiry - a skewed local clock must not re-resolve every time. </summary>
	const float MinSessionAge = 30f;

	/// <summary> Which client's urls refused for a token, and when - so the next resolve skips it (see Forget). </summary>
	class AvoidEntry
	{
		public string Client;
		public RealTimeSince Since;
	}

	static readonly Dictionary<string, Entry> sessions = new();
	static readonly Dictionary<string, AvoidEntry> avoidByToken = new();
	static int directoryCounter;
	static bool cacheWiped;

	public static Task<StreamSession> GetAsync( string token )
	{
		Prune();

		if ( sessions.TryGetValue( token, out var e ) && !e.Task.IsFaulted && !e.Task.IsCanceled && !ExpiringSoon( e ) )
		{
			e.LastUsed = 0;
			return e.Task;
		}

		var entry = new Entry { Task = CreateAsync( token ), LastUsed = 0, Created = 0 };
		sessions[token] = entry;
		entry.Task.FireAndForget( "resolve session" ); // callers see the fault; this keeps it observed if nobody is left waiting
		return entry.Task;
	}

	/// <summary> A finished session whose urls are close to their <c>expire=</c> time: reusing it would hand a backend urls that die within moments. </summary>
	static bool ExpiringSoon( Entry e )
		=> e.Task.IsCompletedSuccessfully && e.Created > MinSessionAge
			&& e.Task.Result.ExpiresAt is { } at && at - DateTimeOffset.UtcNow.ToUnixTimeSeconds() < ReuseExpiryMargin;

	/// <summary>
	/// Forget a token's session (its urls stopped working). With <paramref name="avoidClient"/>, resolving it again
	/// skips that client: its urls passed the probe before dying, so a fresh resolve would walk right back into it.
	/// </summary>
	public static void Forget( string token, string avoidClient = null )
	{
		sessions.Remove( token );
		if ( !string.IsNullOrEmpty( avoidClient ) ) avoidByToken[token] = new AvoidEntry { Client = avoidClient, Since = 0 };
	}

	static void Prune()
	{
		foreach ( var key in sessions.Where( kv => kv.Value.LastUsed > Lifetime ).Select( kv => kv.Key ).ToList() )
			sessions.Remove( key );
		foreach ( var key in avoidByToken.Where( kv => kv.Value.Since > Lifetime ).Select( kv => kv.Key ).ToList() )
			avoidByToken.Remove( key );
	}

	static async Task<StreamSession> CreateAsync( string tokenString )
	{
		var token = PlayToken.Parse( tokenString ) ?? throw new ResolveException( "Bad play token." );
		var extractor = ExtractorRegistry.ByKey( token.Extractor ) ?? throw new ResolveException( $"Unknown media source '{token.Extractor}'." );
		var request = token.ToRequest();
		var ct = CancellationToken.None;

		// this token's urls refused before (see Forget): skip that client for as long as the memory lasts
		if ( avoidByToken.TryGetValue( tokenString, out var avoid ) && avoid.Since < Lifetime )
			request.AvoidClient = avoid.Client;

		var plan = await extractor.GetStreamsAsync( token.Id, request, ct );
		if ( plan.Kind is StreamKind.Direct or StreamKind.Hls or StreamKind.Live ) return new StreamSession { Plan = plan };

		// If YouTube refuses a url later (expired), resolve again and pick the same format
		RemoteFile Remote( StreamFile f ) => new( f.Url, f.Size )
		{
			ClientKey = f.ClientKey,
			Refresh = async ( refused, c ) =>
			{
				// the client whose url just died is the suspect (its fresh urls pass probes all the same): pick the
				// same format from past it. Only the same format fits the index already read (an AV1 MP4 can't
				// continue as a VP9 WebM); null - instead of the stale url - makes RemoteFile give up at once.
				// A url that's only about to expire has no suspect: resolve as usual.
				var fresh = await extractor.GetStreamsAsync( token.Id, refused ? request.Avoiding( f.ClientKey ) : request, c );
				var same = new[] { fresh.Video, fresh.FallbackVideo, fresh.SwitchVideo, fresh.Audio }.FirstOrDefault( x => x?.FormatId == f.FormatId );
				return same?.Url;
			},
		};

		var audioTask = WebmSource.ProbeAsync( Remote( plan.Audio ), ct );
		var videoTask = plan.Kind == StreamKind.Merge ? ProbeVideo() : null;
		var switchTask = plan.Kind == StreamKind.Merge && plan.SwitchVideo is not null ? ProbeSwitch() : null;

		// the video to switch to is optional: without it the AV1 plays as far as YouTube lets it
		async Task<WebmSource> ProbeSwitch()
		{
			try { return await WebmSource.ProbeAsync( Remote( plan.SwitchVideo ), ct ); }
			catch ( Exception e ) when ( e is not OperationCanceledException )
			{
				Log.Warning( $"[bimp] couldn't read the VP9 video to switch to ({e.Message})" );
				return null;
			}
		}

		// an AV1 MP4 that can't be read (refused, or a layout we don't handle) falls back to its VP9 copy
		async Task<WebmSource> ProbeVideo()
		{
			if ( plan.Video.Container != "mp4" ) return await WebmSource.ProbeAsync( Remote( plan.Video ), ct );
			try
			{
				return await WebmSource.ProbeMp4Async( Remote( plan.Video ), ct );
			}
			catch ( Exception e ) when ( e is not OperationCanceledException && plan.FallbackVideo is not null )
			{
				Log.Warning( $"[bimp] couldn't read the AV1 video ({e.Message}), playing VP9" );
				plan.Video = plan.FallbackVideo;
				return await WebmSource.ProbeAsync( Remote( plan.Video ), ct );
			}
		}

		// Await every probe, then report the first failure: a mass refusal (403) fails them all at once, and
		// dropping the others' tasks resurfaced them later as unobserved-exception errors.
		Exception firstFailure = null;
		foreach ( var t in new[] { audioTask, videoTask, switchTask } )
		{
			if ( t is null ) continue;
			try { await t; }
			catch ( Exception e ) { firstFailure ??= e; }
		}

		if ( firstFailure is not null ) throw firstFailure;

		return new StreamSession
		{
			Plan = plan,
			Audio = audioTask.Result,
			Video = videoTask?.Result,
			// whenever it's a lower quality than the video it stands in for (the AV1, or the VP9 4K60 that replaced a
			// failed AV1), for a file that stops answering to carry on with - the AV1 byte budget only applies to the AV1
			SwitchVideo = switchTask is not null && plan.SwitchVideo.FormatId != plan.Video.FormatId ? switchTask.Result : null,
		};
	}

	/// <summary>
	/// A fresh directory in <see cref="FileSystem.Data"/> for one backend's segment files. Leftovers from a previous
	/// run (a crash, or files still open when a player was destroyed) are wiped the first time.
	/// </summary>
	public static string NewCacheDirectory()
	{
		if ( !cacheWiped )
		{
			cacheWiped = true;
			try
			{
				if ( FileSystem.Data.DirectoryExists( "bimp/cache" ) ) FileSystem.Data.DeleteDirectory( "bimp/cache", true );
			}
			catch ( Exception e )
			{
				Log.Trace( $"[bimp] couldn't clear the media cache: {e.Message}" );
			}
		}

		return $"bimp/cache/{++directoryCounter}";
	}
}
