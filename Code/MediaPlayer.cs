using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bimp.Resolver;

namespace Bimp;

/// <summary>
/// A networked media player. The host owns the state (what's playing, when it started, paused, queue)
/// and every client plays it locally, keeping itself in sync with the host's clock.
/// <para>
/// Put this on a GameObject with a <see cref="WorldPanel"/> + <see cref="MediaScreen"/> for a TV/screen,
/// or with <see cref="MediaSpeaker"/> and <see cref="AudioOnlyPlayer"/> for a speaker. Add a collider and
/// <see cref="MediaInteract"/> so players can open the controls with the use key.
/// </para>
/// </summary>
[Title( "Media Player" ), Category( "Media" ), Icon( "smart_display" )]
public sealed class MediaPlayer : Component
{
	//
	// Inspector settings
	//

	/// <summary>
	/// Who can control playback. The host can always control it.
	/// </summary>
	[Property, Group( "Access" )] public MediaPermission Permission { get; set; } = MediaPermission.Anyone;

	/// <summary>
	/// SteamIds allowed to control this player when <see cref="Permission"/> is Whitelist.
	/// </summary>
	[Property, Group( "Access" ), ShowIf( nameof( Permission ), MediaPermission.Whitelist )]
	public List<long> AllowedSteamIds { get; set; } = new();

	/// <summary>
	/// Let anyone add to the queue, even if they can't control playback.
	/// </summary>
	[Property, Group( "Access" )] public bool AnyoneCanQueue { get; set; } = true;

	[Property, Group( "Queue" ), Range( 1, 200 )] public int MaxQueue { get; set; } = 30;

	/// <summary>
	/// Replay the current item when it ends and the queue is empty.
	/// </summary>
	[Property, Group( "Queue" )] public bool Loop { get; set; }

	/// <summary>
	/// Played by the host when the scene starts.
	/// </summary>
	[Property, Group( "Playback" )] public string DefaultUrl { get; set; }

	/// <summary>
	/// Never show video, only play audio. For speakers, radios and jukeboxes.
	/// </summary>
	[Property, Group( "Playback" )] public bool AudioOnlyPlayer { get; set; }

	[Property, Group( "Playback" ), Range( 0, 1 )] public float Volume { get; set; } = 1.0f;

	/// <summary>
	/// Sound comes from this object's position in the world and falls off with distance.
	/// When off, everyone hears it at the same volume wherever they are.
	/// </summary>
	[Property, Group( "Audio" )] public bool Spatial { get; set; } = true;

	/// <summary>
	/// Distance at which the sound is fully faded out (see <see cref="AudioFalloff"/>).
	/// </summary>
	[Property, Group( "Audio" ), ShowIf( nameof( Spatial ), true )] public float AudioDistance { get; set; } = 4000.0f;

	/// <summary>
	/// How the sound fades out to <see cref="AudioDistance"/>: nearly full volume in the room with the screen, then a
	/// smooth fade. The engine's own curve is made for its 15000 unit default range; over a screen's few thousand units
	/// it was down to 22% three metres away and 4% at twelve.
	/// </summary>
	public static readonly Curve AudioFalloff = new( new( 0, 1 ), new( 0.15f, 0.92f ), new( 0.45f, 0.55f ), new( 0.75f, 0.2f ), new( 1, 0 ) );

	/// <summary> 3D for this player: the player's own setting, unless they've turned 3D audio off (bimp_spatial). </summary>
	public bool EffectiveSpatial => Spatial && MediaSettings.SpatialAudio;

	/// <summary>
	/// 2D for this player (bimp_spatial off, on a player that's otherwise 3D): the sound has no direction, but still
	/// fades with the listener's distance from it, on the same curve as 3D. A player that isn't <see cref="Spatial"/>
	/// at all is the same volume everywhere.
	/// </summary>
	bool FlatWithDistance => Spatial && !MediaSettings.SpatialAudio;

	/// <summary> How loud this player is at a point, from <see cref="AudioFalloff"/> over <see cref="AudioDistance"/>. </summary>
	float DistanceFade( Vector3 listener )
	{
		var range = ScaledAudioDistance;
		if ( range <= 0 ) return 1;
		return AudioFalloff.Evaluate( (listener.Distance( SoundPositionFor( listener ) ) / range).Clamp( 0, 1 ) );
	}

	/// <summary>
	/// Where the sound comes from. Defaults to this GameObject.
	/// </summary>
	[Property, Group( "Audio" )] public GameObject SoundOrigin { get; set; }

	//
	// Synced state - only the host writes these
	//

	/// <summary> The url the user entered. </summary>
	[Sync( SyncFlags.FromHost )] public string CurrentUrl { get; set; }

	/// <summary> The url clients actually play. Empty when nothing is playing. </summary>
	[Sync( SyncFlags.FromHost )] public string PlayUrl { get; set; }

	[Sync( SyncFlags.FromHost )] public string Title { get; set; }
	[Sync( SyncFlags.FromHost )] public string RequestedBy { get; set; }

	/// <summary> Media length in seconds, 0 if unknown or live. </summary>
	[Sync( SyncFlags.FromHost )] public float MediaDuration { get; set; }

	[Sync( SyncFlags.FromHost )] public bool IsLive { get; set; }
	[Sync( SyncFlags.FromHost )] public bool AudioOnly { get; set; }
	[Sync( SyncFlags.FromHost )] public bool SeekByReload { get; set; }

	/// <summary> Host time (<see cref="Time.NowDouble"/>) at which media time 0 would have played. </summary>
	[Sync( SyncFlags.FromHost )] public double StartTime { get; set; }

	[Sync( SyncFlags.FromHost )] public bool Paused { get; set; }
	[Sync( SyncFlags.FromHost )] public float PausedAt { get; set; }

	/// <summary> Increments every time something (re)starts, so replaying the same url reloads it. </summary>
	[Sync( SyncFlags.FromHost )] public int PlayId { get; set; }

	/// <summary> Increments on every explicit seek, so clients apply it right away rather than treating it as drift. </summary>
	[Sync( SyncFlags.FromHost )] public int SeekId { get; set; }

	/// <summary> Host status message: "Loading..." or an error. </summary>
	[Sync( SyncFlags.FromHost )] public string Status { get; set; }

	[Sync( SyncFlags.FromHost )] public NetList<MediaQueueItem> Queue { get; set; } = new();

	/// <summary> Video heights the current media is available in (see <see cref="MediaQueueItem.Qualities"/>). </summary>
	[Sync( SyncFlags.FromHost )] public string Qualities { get; set; }

	/// <summary> Audio tracks of the current media (see <see cref="MediaQueueItem.AudioTracks"/>). </summary>
	[Sync( SyncFlags.FromHost )] public string AudioTracks { get; set; }

	/// <summary> Caption tracks of the current media (see <see cref="MediaQueueItem.CaptionTracks"/>). </summary>
	[Sync( SyncFlags.FromHost )] public string CaptionTracks { get; set; }

	//
	// Per-client stream choices. Everyone shares the timeline, but picks their own resolution / audio language.
	//

	public IReadOnlyList<int> AvailableQualities => MediaStreamOptions.ParseQualities( Qualities );
	public IReadOnlyList<MediaAudioTrack> AvailableAudioTracks => MediaStreamOptions.ParseAudioTracks( AudioTracks );

	/// <summary> The height this client streams, 0 for automatic. From the bimp_quality convar. </summary>
	public int SelectedQuality => MediaStreamOptions.ChooseQuality( AvailableQualities, MediaSettings.Quality );

	/// <summary> The audio track this client streams, null for the original. From the bimp_audio_lang convar. </summary>
	public MediaAudioTrack SelectedAudioTrack => MediaStreamOptions.ChooseAudioTrack( AvailableAudioTracks, MediaSettings.AudioLanguage );

	/// <summary> <see cref="PlayUrl"/> plus this client's choices. Changing a choice reloads playback at the same time. </summary>
	public string LocalStreamUrl => MediaStreamOptions.ApplyTo( PlayUrl, SelectedQuality, SelectedAudioTrack );

	string CurrentKey => KeyFor( PlayId, LocalStreamUrl );

	//
	// Captions. Every client fetches its own cues (the urls can be tied to the IP) and shows them against the shared timeline.
	//

	public IReadOnlyList<MediaCaptionTrack> AvailableCaptionTracks => Captions.ParseTracks( CaptionTracks );

	/// <summary> The caption track this client shows, null when captions are off or there are none. From the bimp_subs / bimp_sub_lang convars. </summary>
	public MediaCaptionTrack SelectedCaptionTrack => MediaSettings.Subtitles ? Captions.Choose( AvailableCaptionTracks, MediaSettings.SubtitleLanguage ) : null;

	/// <summary> The caption line to show right now, null for none. </summary>
	public string CurrentCaption => HasMedia ? Captions.TextAt( captionCues, CurrentTime ) : null;

	string captionKey;
	int captionToken;
	List<CaptionCue> captionCues;

	/// <summary> Fetch the cues when the media or the chosen track changes. </summary>
	void UpdateCaptions()
	{
		var track = HasMedia ? SelectedCaptionTrack : null;
		var key = track is null ? null : $"{PlayId}|{track.Id}";
		if ( key == captionKey ) return;

		captionKey = key;
		captionCues = null;
		var token = ++captionToken;
		if ( track is not null ) _ = LoadCaptionsAsync( token, track );
	}

	async Task LoadCaptionsAsync( int token, MediaCaptionTrack track )
	{
		try
		{
			var play = Bimp.Resolver.PlayToken.Parse( PlayUrl );
			if ( Bimp.Resolver.Extractors.ExtractorRegistry.ByKey( play?.Extractor ) is not Bimp.Resolver.ICaptionExtractor source ) return;

			var body = await source.GetCaptionsAsync( play.Id, track.Id, System.Threading.CancellationToken.None );
			if ( token != captionToken || !this.IsValid() ) return;

			var cues = Captions.Parse( body );
			Log.Info( $"[bimp] captions {track.Id} ({track.Name}): {cues.Count} cues" );
			captionCues = cues;
		}
		catch ( Exception e )
		{
			Log.Warning( $"[bimp] Couldn't load captions: {e.Message}" );
		}
	}

	//
	// Local state
	//

	/// <summary>
	/// The local playback. Null on a dedicated server or when nothing is playing.
	/// </summary>
	public MediaBackend Backend { get; private set; }

	string backendKey;
	RealTimeSince sinceCorrection;
	bool initialSeekPending;
	int reportedDurationFor = -1;
	int endedPlayId = -1;
	int resolveToken;

	/// <summary>
	/// Local message for this client (permission denied, load failures), fades after a few seconds.
	/// </summary>
	public string LocalNotice { get; private set; }
	RealTimeSince sinceNotice;

	public bool HasMedia => !string.IsNullOrEmpty( PlayUrl );

	/// <summary>
	/// Are we the authority on the state? True for the host, or when not networked at all.
	/// </summary>
	public bool IsAuthority => !Networking.IsActive || Networking.IsHost;

	/// <summary>
	/// Where playback should be right now, according to the host.
	/// </summary>
	public float CurrentTime
	{
		get
		{
			if ( !HasMedia ) return 0;
			var t = Paused ? PausedAt : (float)Math.Max( 0, Time.NowDouble - StartTime );
			if ( MediaDuration > 0 ) t = MathF.Min( t, MediaDuration );
			return t;
		}
	}

	/// <summary>
	/// The shared resolver (host only).
	/// </summary>
	static BimpResolver Resolver => BimpResolver.Instance;

	public Vector3 SoundPosition => SoundOrigin.IsValid() ? SoundOrigin.WorldPosition : WorldPosition;

	BoxCollider soundBox;

	/// <summary> The object the sound comes from: <see cref="SoundOrigin"/>, or this one. </summary>
	GameObject SoundObject => SoundOrigin.IsValid() ? SoundOrigin : GameObject;

	/// <summary>
	/// How much bigger (or smaller) than its prefab this player has been scaled, e.g. by the Sandbox resize tool.
	/// The audio range grows with it, so a bigger screen is heard from further away.
	/// </summary>
	public float SoundScale
	{
		get
		{
			var s = SoundObject.WorldScale;
			return MathF.Max( 0.01f, MathF.Max( MathF.Abs( s.x ), MathF.Max( MathF.Abs( s.y ), MathF.Abs( s.z ) ) ) );
		}
	}

	/// <summary> <see cref="AudioDistance"/> at the current size. </summary>
	public float ScaledAudioDistance => AudioDistance * SoundScale;

	/// <summary>
	/// Where the sound is heard from for a listener. A scaled-up screen is a big surface, not a point, so the sound comes
	/// from the nearest point of its box (the listener's own position when inside it), and fades from there.
	/// </summary>
	Vector3 SoundPositionFor( Vector3 listener )
	{
		if ( SoundOrigin.IsValid() ) return SoundOrigin.WorldPosition;

		if ( !soundBox.IsValid() ) soundBox = GetComponent<BoxCollider>();
		if ( !soundBox.IsValid() ) return WorldPosition;

		var tx = soundBox.GameObject.WorldTransform;
		var half = soundBox.Scale * 0.5f;
		var local = tx.PointToLocal( listener ) - soundBox.Center;
		local = new Vector3( local.x.Clamp( -half.x, half.x ), local.y.Clamp( -half.y, half.y ), local.z.Clamp( -half.z, half.z ) );
		return tx.PointToWorld( local + soundBox.Center );
	}

	protected override void OnAwake()
	{
		// Sync'd properties only work on network objects. Make that the default so mappers don't have to.
		if ( GameObject.NetworkMode == NetworkMode.Snapshot )
			GameObject.NetworkMode = NetworkMode.Object;
	}

	protected override void OnStart()
	{
		if ( IsAuthority && !HasMedia && !string.IsNullOrWhiteSpace( DefaultUrl ) )
		{
			_ = PlayNowAsync( DefaultUrl, "Map" );
		}
	}

	protected override void OnDestroy()
	{
		DisposeBackend();
	}

	protected override void OnDisabled()
	{
		DisposeBackend();
	}

	protected override void OnUpdate()
	{
		if ( IsAuthority )
			UpdateAuthority();

		if ( !Application.IsDedicatedServer )
		{
			UpdatePlayback();
			UpdateCaptions();
			RecentLinks.Saw( this );
		}

		MediaProbe.PresentFile();
		LatencyLab.Tick();
		MediaProbe.SampleLatency( this );
		if ( MediaProbe.IsRecording( this ) )
			MediaProbe.Sample( this );

		if ( LocalNotice is not null && sinceNotice > 6 )
			LocalNotice = null;
	}

	#region Permissions

	/// <summary>
	/// Can this connection control playback?
	/// </summary>
	public bool CanControl( Connection c )
	{
		if ( !Networking.IsActive ) return true;
		if ( c is null ) return false;
		if ( c.IsHost || c == Connection.Host ) return true;

		return Permission switch
		{
			MediaPermission.Anyone => true,
			MediaPermission.Whitelist => AllowedSteamIds?.Contains( c.SteamId.Value ) ?? false,
			_ => false,
		};
	}

	/// <summary>
	/// Can this connection add to the queue?
	/// </summary>
	public bool CanQueue( Connection c ) => AnyoneCanQueue || CanControl( c );

	/// <summary>
	/// Can the local player control playback? Used by the UI.
	/// </summary>
	public bool CanLocalControl => CanControl( Connection.Local );
	public bool CanLocalQueue => CanQueue( Connection.Local );

	static string CallerName( Connection c ) => c?.DisplayName ?? "Someone";

	#endregion

	#region Requests (called by anyone, run on the host)

	/// <summary>
	/// Play this url now, replacing whatever is playing.
	/// </summary>
	[Rpc.Host]
	public void RequestPlay( string url )
	{
		var caller = Rpc.Caller;
		if ( !CanControl( caller ) ) { Deny( caller, "You don't have permission to control this player." ); return; }

		_ = PlayNowAsync( url, CallerName( caller ) );
	}

	/// <summary>
	/// Add this url to the queue. Plays immediately if nothing is playing.
	/// </summary>
	[Rpc.Host]
	public void RequestEnqueue( string url )
	{
		var caller = Rpc.Caller;
		if ( !CanQueue( caller ) ) { Deny( caller, "You don't have permission to queue on this player." ); return; }
		if ( Queue.Count >= MaxQueue ) { Deny( caller, "The queue is full." ); return; }

		_ = EnqueueAsync( url, CallerName( caller ), caller );
	}

	[Rpc.Host]
	public void RequestSetPaused( bool paused )
	{
		if ( !CanControl( Rpc.Caller ) ) { Deny( Rpc.Caller, "You don't have permission to control this player." ); return; }
		SetPaused( paused );
	}

	[Rpc.Host]
	public void RequestSeek( float time )
	{
		if ( !CanControl( Rpc.Caller ) ) { Deny( Rpc.Caller, "You don't have permission to control this player." ); return; }
		Seek( time );
	}

	[Rpc.Host]
	public void RequestSkip()
	{
		if ( !CanControl( Rpc.Caller ) ) { Deny( Rpc.Caller, "You don't have permission to control this player." ); return; }
		PlayNext();
	}

	[Rpc.Host]
	public void RequestStop()
	{
		if ( !CanControl( Rpc.Caller ) ) { Deny( Rpc.Caller, "You don't have permission to control this player." ); return; }
		Stop();
	}

	[Rpc.Host]
	public void RequestRemove( int index )
	{
		if ( !CanControl( Rpc.Caller ) ) { Deny( Rpc.Caller, "You don't have permission to control this player." ); return; }
		if ( index < 0 || index >= Queue.Count ) return;
		Queue.RemoveAt( index );
	}

	[Rpc.Host]
	public void RequestClearQueue()
	{
		if ( !CanControl( Rpc.Caller ) ) { Deny( Rpc.Caller, "You don't have permission to control this player." ); return; }
		Queue.Clear();
	}

	/// <summary>
	/// A client learned the duration of a direct file after loading it. The host takes the first answer.
	/// </summary>
	[Rpc.Host( NetFlags.Unreliable )]
	void ReportDuration( int playId, float duration )
	{
		if ( playId != PlayId || MediaDuration > 0 || IsLive ) return;
		if ( duration <= 0 || float.IsNaN( duration ) || float.IsInfinity( duration ) ) return;
		MediaDuration = duration;
	}

	/// <summary>
	/// Tell one client something went wrong with their request.
	/// </summary>
	void Deny( Connection caller, string message )
	{
		if ( caller is null || caller == Connection.Local || !Networking.IsActive )
		{
			ShowNotice( message );
			return;
		}

		using ( Rpc.FilterInclude( caller ) )
		{
			ReceiveNotice( message );
		}
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	void ReceiveNotice( string message )
	{
		ShowNotice( message );
	}

	void ShowNotice( string message )
	{
		LocalNotice = message;
		sinceNotice = 0;
	}

	#endregion

	#region Authority (host) logic

	/// <summary>
	/// Resolve and play right away. Host only.
	/// </summary>
	public async Task PlayNowAsync( string url, string requestedBy )
	{
		if ( !IsAuthority ) return;

		var token = ++resolveToken;
		Status = "Loading...";

		try
		{
			// a playlist link: the first video plays now, the rest are queued
			var playlist = await Resolver.ResolvePlaylistAsync( url, AudioOnlyPlayer, MaxQueue + 1 );
			if ( token != resolveToken || !this.IsValid() ) return;
			if ( playlist is not null )
			{
				var first = await ResolveLazyAsync( playlist[0], requestedBy );
				if ( token != resolveToken || !this.IsValid() ) return;

				StartItem( first );
				QueueLazy( playlist.Skip( 1 ), requestedBy, null );
				return;
			}

			var item = await Resolver.ResolveAsync( url, AudioOnlyPlayer );
			if ( token != resolveToken || !this.IsValid() ) return; // something newer was requested

			item.RequestedBy = requestedBy;
			StartItem( item );
		}
		catch ( Exception e )
		{
			if ( token != resolveToken || !this.IsValid() ) return;
			Status = e is ResolveException ? e.Message : $"Error: {e.Message}";
			Log.Warning( $"[bimp] Couldn't play {url}: {(e is ResolveException ? e.Message : e.ToString())}" );
		}
	}

	/// <summary>
	/// Resolve and add to the queue. Host only.
	/// </summary>
	public async Task EnqueueAsync( string url, string requestedBy, Connection caller = null )
	{
		if ( !IsAuthority ) return;

		try
		{
			var playlist = await Resolver.ResolvePlaylistAsync( url, AudioOnlyPlayer, MaxQueue + 1 );
			if ( !this.IsValid() ) return;
			if ( playlist is not null )
			{
				var rest = playlist.AsEnumerable();
				if ( !HasMedia && Status != "Loading..." )
				{
					var first = await ResolveLazyAsync( playlist[0], requestedBy );
					if ( !this.IsValid() ) return;
					if ( !HasMedia && Status != "Loading..." ) // nothing else started meanwhile
					{
						StartItem( first );
						rest = playlist.Skip( 1 );
					}
				}

				QueueLazy( rest, requestedBy, caller );
				return;
			}

			var item = await Resolver.ResolveAsync( url, AudioOnlyPlayer );
			if ( !this.IsValid() ) return;

			item.RequestedBy = requestedBy;

			if ( !HasMedia && Status != "Loading..." )
			{
				StartItem( item );
				return;
			}

			if ( Queue.Count >= MaxQueue ) { Deny( caller, "The queue is full." ); return; }
			Queue.Add( item );
		}
		catch ( Exception e )
		{
			if ( !this.IsValid() ) return;
			Deny( caller, e is ResolveException ? e.Message : $"Error: {e.Message}" );
		}
	}

	/// <summary> A playlist entry resolved properly (qualities, dubs, live or not). Throws like <see cref="BimpResolver.ResolveAsync"/>. </summary>
	async Task<MediaQueueItem> ResolveLazyAsync( MediaQueueItem entry, string requestedBy )
	{
		var item = await Resolver.ResolveAsync( entry.Url, AudioOnlyPlayer );
		item.RequestedBy = requestedBy;
		return item;
	}

	/// <summary> Add playlist entries to the queue until it's full, and say so if some didn't fit. </summary>
	void QueueLazy( IEnumerable<MediaQueueItem> entries, string requestedBy, Connection caller )
	{
		int added = 0, left = 0;
		foreach ( var entry in entries )
		{
			if ( Queue.Count >= MaxQueue ) { left++; continue; }
			entry.RequestedBy = requestedBy;
			Queue.Add( entry );
			added++;
		}

		if ( left > 0 ) Deny( caller, $"Queued {added} videos - the queue is full, {left} more didn't fit." );
	}

	/// <summary> Play a queued playlist entry: resolve it first, and skip it (with a notice) if it can't be played. </summary>
	async Task StartLazyAsync( MediaQueueItem entry )
	{
		var token = resolveToken;
		Status = "Loading...";

		try
		{
			var item = await ResolveLazyAsync( entry, entry.RequestedBy );
			if ( token != resolveToken || !this.IsValid() ) return;
			StartItem( item );
		}
		catch ( Exception e )
		{
			if ( token != resolveToken || !this.IsValid() ) return;

			var why = e is ResolveException ? e.Message : $"Error: {e.Message}";
			Log.Warning( $"[bimp] Couldn't play {entry.Url}: {e.Message}" );

			var notice = $"Skipped \"{entry.Title}\": {why}";
			if ( Networking.IsActive ) ReceiveNotice( notice );
			else ShowNotice( notice );

			PlayNext();
		}
	}

	void StartItem( MediaQueueItem item )
	{
		CurrentUrl = item.Url;
		PlayUrl = item.PlayUrl;
		Title = item.Title;
		RequestedBy = item.RequestedBy;
		MediaDuration = item.Duration;
		IsLive = item.IsLive;
		AudioOnly = item.AudioOnly || AudioOnlyPlayer;
		SeekByReload = item.SeekByReload;
		Qualities = item.Qualities;
		AudioTracks = item.AudioTracks;
		CaptionTracks = item.CaptionTracks;
		Paused = false;
		PausedAt = 0;
		// a link with a start time (?t=90) plays from there, the way a late joiner starts mid-way
		StartTime = Time.NowDouble - (item.IsLive ? 0 : Math.Max( 0, item.StartAt ));
		Status = null;
		PlayId++;
	}

	/// <summary>
	/// Play the next item in the queue, or stop. Host only.
	/// </summary>
	public void PlayNext()
	{
		if ( !IsAuthority ) return;

		resolveToken++; // cancel any pending "play now"

		if ( Queue.Count > 0 )
		{
			var next = Queue[0];
			Queue.RemoveAt( 0 );
			if ( next.Lazy ) _ = StartLazyAsync( next );
			else StartItem( next );
			return;
		}

		Stop();
	}

	/// <summary>
	/// Stop playback (the queue is kept). Host only.
	/// </summary>
	public void Stop()
	{
		if ( !IsAuthority ) return;

		resolveToken++;
		CurrentUrl = null;
		PlayUrl = null;
		Title = null;
		RequestedBy = null;
		Qualities = null;
		AudioTracks = null;
		CaptionTracks = null;
		MediaDuration = 0;
		IsLive = false;
		Paused = false;
		PausedAt = 0;
		Status = null;
		PlayId++;
	}

	public void SetPaused( bool paused )
	{
		if ( !IsAuthority || !HasMedia || IsLive || Paused == paused ) return;

		if ( paused )
		{
			// Freeze the shared timeline where the host's picture actually is, so the host's own video
			// doesn't jump on pause. Clients are within their drift tolerance of it anyway.
			PausedAt = LocalPlaybackPosition() ?? CurrentTime;
			Paused = true;
		}
		else
		{
			StartTime = Time.NowDouble - PausedAt;
			Paused = false;
		}
	}

	public void Seek( float time )
	{
		if ( !IsAuthority || !HasMedia || IsLive ) return;

		time = MathF.Max( 0, time );
		if ( MediaDuration > 0 ) time = MathF.Min( time, MediaDuration - 0.5f );

		if ( Paused ) PausedAt = time;
		else StartTime = Time.NowDouble - time;

		SeekId++;

		// Don't let an old "ended" state trip us up after seeking back
		endedPlayId = -1;
	}

	void UpdateAuthority()
	{
		if ( !HasMedia || Paused || IsLive ) return;
		if ( endedPlayId == PlayId ) return;

		var ended = false;

		// Known duration - use the clock, works on dedicated servers too
		if ( MediaDuration > 0 && Time.NowDouble - StartTime >= MediaDuration + 1.0 )
			ended = true;

		// Unknown duration - trust our own playback, if we have one
		if ( MediaDuration <= 0 && Backend is not null && Backend.Finished && backendKey == CurrentKey )
			ended = true;

		if ( !ended ) return;

		endedPlayId = PlayId;
		OnMediaEnded();
	}

	void OnMediaEnded()
	{
		if ( Queue.Count > 0 )
		{
			PlayNext();
			return;
		}

		if ( Loop )
		{
			Paused = false;
			StartTime = Time.NowDouble;
			PlayId++; // force everyone to reload, the decoder has stopped
			return;
		}

		Stop();
	}

	#endregion

	#region Local playback

	static string KeyFor( int playId, string url ) => string.IsNullOrEmpty( url ) ? null : $"{playId}|{url}";

	/// <summary>
	/// Throw away local playback and start it again at the synced time.
	/// </summary>
	internal void ReloadBackend()
	{
		DisposeBackend();
		backendKey = CurrentKey;
		if ( backendKey is not null ) CreateBackend();
	}

	/// <summary>
	/// Diagnostics: open merged media at t with an explicit time offset.
	/// </summary>
	internal void ReloadBackendAt( float t, float offset )
	{
		DisposeBackend();
		backendKey = CurrentKey;
		if ( backendKey is null ) return;
		Backend = PlayToken.IsToken( LocalStreamUrl )
			? MediaBackend.CreateNative( LocalStreamUrl, AudioOnly, t )
			: MediaBackend.Create( LocalStreamUrl, AudioOnly, offset );
		sinceCorrection = 0;
		initialSeekPending = false;
		appliedSeekId = SeekId;
		lastAppliedPaused = !Paused;
	}

	void DisposeBackend()
	{
		Backend?.Dispose();
		Backend = null;
		backendKey = null;
	}

	void CreateBackend()
	{
		// Merged media starts at the synced time: segment files are written from the cue point before it,
		// with absolute timestamps, so there's no time offset. Everything else starts at 0 and seeks once loaded.
		var t = CurrentTime;
		var url = LocalStreamUrl;

		Backend = IsLive ? MediaBackend.CreateLive( url, Math.Clamp( MediaSettings.MaxVideoHeight, 144, 2160 ), AudioOnly )
			: PlayToken.IsToken( url ) ? MediaBackend.CreateNative( url, AudioOnly, SeekByReload && !IsLive ? t : 0 )
			: MediaBackend.Create( url, AudioOnly, 0 );
		sinceCorrection = 0;
		initialSeekPending = !IsLive; // once loaded, land exactly on the synced time (see CorrectDrift)
		appliedSeekId = SeekId; // a fresh backend starts at the right time anyway (initial seek)
		lastAppliedPaused = !Paused; // make sure the new backend gets told the pause state

		if ( Backend.Error is not null )
		{
			notifiedError = Backend;
			ShowNotice( $"Can't play this: {Backend.Error}" );
		}
	}

	void UpdatePlayback()
	{
		var wantKey = CurrentKey;

		if ( wantKey != backendKey )
		{
			DisposeBackend();
			backendKey = wantKey;

			if ( wantKey is not null )
				CreateBackend();
		}

		if ( Backend is null ) return;

		Backend.SyncedTime = Paused || IsLive ? null : CurrentTime;
		Backend.Present();
		var volume = Volume * MediaSettings.EffectiveVolume;
		if ( FlatWithDistance ) volume *= DistanceFade( Sound.Listener.Position );
		Backend.SetAudio( SoundPositionFor( Sound.Listener.Position ), EffectiveSpatial, volume, ScaledAudioDistance );

		if ( Backend.Error is not null )
		{
			// native media fails after the backend was created (resolving / downloading) - say so once
			if ( notifiedError != Backend )
			{
				notifiedError = Backend;
				ShowNotice( $"Can't play this: {Backend.Error}" );
			}
			return;
		}

		if ( !Backend.Loaded )
		{
			if ( Backend.SinceCreated > 20 && LocalNotice is null )
				ShowNotice( "This is taking a while to load..." );
			return;
		}

		// Tell the host the duration of direct files, it can't know until something loads them
		if ( MediaDuration <= 0 && !IsLive && !SeekByReload && reportedDurationFor != PlayId && Backend.Duration > 0 )
		{
			reportedDurationFor = PlayId;
			ReportDuration( PlayId, Backend.Duration );
		}

		// Pausing/resuming gets a moment to settle (the decoder refills its buffers on resume) before
		// anything is judged as drift or a stall - correcting then is what caused stutter after unpausing
		// A freshly opened player (after a seek, join or reload) runs until it has landed on the synced time,
		// even when paused - otherwise a seek while paused leaves the screen without the new frame
		var wantPaused = Paused && !initialSeekPending;
		if ( wantPaused != lastAppliedPaused )
		{
			lastAppliedPaused = wantPaused;
			sinceCorrection = 0;
			ResetStallTimer();
		}

		Backend.SetPaused( wantPaused );

		// A stuck live stream (source hiccup, dropped connection) reconnects at the live edge
		if ( CheckStalled() ) return;

		if ( IsLive ) return; // nothing to sync to - everyone watches the live edge

		CorrectDrift();
	}

	bool lastAppliedPaused;
	int appliedSeekId;
	MediaBackend notifiedError;

	/// <summary>
	/// Where this client's playback actually is, if it's loaded and roughly in sync (null otherwise).
	/// </summary>
	float? LocalPlaybackPosition()
	{
		if ( Backend is null || Backend.Error is not null || !Backend.Loaded || backendKey != CurrentKey ) return null;

		var t = Backend.Time;
		if ( MathF.Abs( t - CurrentTime ) > 3.0f ) return null; // way off (stalled / still seeking) - don't trust it
		if ( MediaDuration > 0 ) t = MathF.Min( t, MediaDuration );
		return MathF.Max( 0, t );
	}

	const float StallSeconds = 5.0f;
	const int MaxStallReloads = 3;

	float lastProgressTime;
	RealTimeSince sinceProgress;
	int stallReloads;
	int stallPlayId = -1;

	/// <summary>
	/// If playback should be moving but hasn't for a while (the decoder got stuck after a seek,
	/// the connection dropped...), throw the backend away and start again at the right time.
	/// Returns true if it reloaded.
	/// </summary>
	bool CheckStalled()
	{
		if ( stallPlayId != PlayId )
		{
			stallPlayId = PlayId;
			stallReloads = 0;
			ResetStallTimer();
		}

		var t = Backend.Time;
		if ( Paused || MathF.Abs( t - lastProgressTime ) > 0.05f )
		{
			lastProgressTime = t;
			sinceProgress = 0;
			return false;
		}

		// Reached the end, not stuck
		if ( MediaDuration > 0 && t >= MediaDuration - 1.0f ) return false;

		if ( sinceProgress < StallSeconds || stallReloads >= MaxStallReloads ) return false;

		stallReloads++;
		Log.Warning( $"[bimp] Playback stalled at {t:0.0}s, reloading ({stallReloads}/{MaxStallReloads})" );
		ShowNotice( "Playback got stuck, reloading..." );

		DisposeBackend();
		backendKey = CurrentKey;
		CreateBackend();
		ResetStallTimer();
		return true;
	}

	void ResetStallTimer()
	{
		lastProgressTime = -1;
		sinceProgress = 0;
	}

	/// <summary> Merged media lands within this of the synced time without seeking (the drift threshold). </summary>
	const float MergedLandingTolerance = 1.5f;

	/// <summary> The play and seek that merged media last reopened for, after landing too far behind. </summary>
	(int playId, int seekId) landingReloadFor = (-1, -1);

	/// <summary>
	/// Keep local playback close to where the host says it should be.
	/// </summary>
	void CorrectDrift()
	{
		var expected = CurrentTime;
		var drift = Backend.Time - expected;
		var absDrift = MathF.Abs( drift );

		// Just loaded: land exactly on the synced time. For merged media the first segment starts at the keyframe
		// cluster before it, so this is a short hop forward inside the first cluster (a few seconds at most),
		// which the engine's player gets through in a blink.
		if ( initialSeekPending )
		{
			// The loaded event can come a frame or two before the player's clock reports the segment's position
			// (it reads 0) - wait for it, or we'd skip the hop and sit a second behind
			if ( SeekByReload && absDrift >= 15.0f && Backend.SinceCreated < 10.0f ) return;

			initialSeekPending = false;
			appliedSeekId = SeekId;
			// Merged media started at a keyframe just behind the synced time (or waited for the next one): it keeps that
			// small lag rather than seeking forward, which means decoding everything in between while the sound runs
			// on - at 4K the picture then crawled behind for seconds (see MediaBackend.StartNative).
			MediaProbe.Note( $"initial landing: drift {drift * 1000:0}ms" );
			var tolerance = SeekByReload ? MergedLandingTolerance : 0.25f;
			if ( absDrift > tolerance && (!SeekByReload || absDrift < 15.0f) )
			{
				// Landed further behind than planned (a slow load): open again, now planned with the real load time,
				// rather than seek forward. Once per play or seek, so a load that's always slow can't loop.
				if ( SeekByReload && drift < 0 && landingReloadFor != (PlayId, SeekId) )
				{
					landingReloadFor = (PlayId, SeekId);
					MediaProbe.Note( "landed too far behind: reopening" );
					ReloadBackend();
					return;
				}

				Backend.Seek( expected );
				sinceCorrection = 0;
			}
			return;
		}

		// Someone seeked (button / progress bar): apply it now, not as drift after a cooldown
		var explicitSeek = appliedSeekId != SeekId;
		appliedSeekId = SeekId;

		// The same tolerance applies while paused - pausing must never seek (our playback normally trails
		// the synced clock by up to a second of load latency; correcting that on pause made it jump).
		const float threshold = 1.5f;
		var drifted = absDrift >= threshold && sinceCorrection >= 3.0f;
		if ( !(explicitSeek && absDrift > 0.25f) && !drifted ) return;

		if ( SeekByReload )
		{
			// ROOT CAUSE of "it keeps skipping forward frames": the engine's player can't seek forward in a
			// stream. Seek() jumps its clock but it keeps reading and decoding sequentially from where it was,
			// throwing away every frame as late until it catches up - fast forwarding for as long as the gap
			// was (minutes, for a big seek), while drift correction kept firing more seeks. So merged
			// media never asks it to jump: it reopens at the target, writing segment files that start there.
			if ( MediaDuration > 0 && expected >= MediaDuration - 2 ) return;
			ReloadBackend();
			return;
		}

		// Direct files: the native seek (fine for backward seeks and short hops)
		Backend.Seek( expected );
		sinceCorrection = 0;
	}

	#endregion
}
