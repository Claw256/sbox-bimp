using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bimp.Resolver.Live;

/// <summary>
/// An MPEG-DASH manifest (static, fragmented MP4) turned into the HLS playlists <see cref="HlsReader"/> already
/// reads: a master playlist with one variant per video representation and the audio as a separate rendition, and a
/// media playlist per representation (<c>#EXT-X-MAP</c> for the init segment, <c>#EXT-X-BYTERANGE</c> for
/// single-file representations). Variant choice, audio/video alignment, VOD start times and fMP4 feeding all come
/// from the HLS path. The playlists are made up on the spot: <see cref="FetchAsync"/> answers for their urls.
/// <para>
/// Supported: one period; <c>SegmentTemplate</c> (timeline, or a fixed duration), <c>SegmentList</c> and
/// <c>SegmentBase</c> with a <c>sidx</c> index; H.264 / AV1 video and AAC audio in MP4. Not supported: live (dynamic)
/// manifests, several periods, WebM, encrypted media.
/// </para>
/// </summary>
public sealed class MpdPlaylists
{
	/// <summary> The master playlist's (made up) url - give it to <see cref="HlsReader"/>. </summary>
	public Uri Master { get; }

	/// <summary> Seconds; 0 if the manifest doesn't say. </summary>
	public double Duration { get; }

	public string Title { get; }

	/// <summary> The video heights on offer, highest first. </summary>
	public List<int> Heights { get; }

	sealed class Rep
	{
		public string Id, Codecs, Lang, PlaylistName;
		public long Bandwidth;
		public int Width, Height;
		public bool Video;
		public Uri Base;
		public Node Element, Set, Period;
	}

	/// <summary>
	/// One XML element - just what a manifest needs. System.Xml is off the sandbox's whitelist, so this reads the XML
	/// itself: names are local names (prefixes dropped), xmlns declarations are skipped, comments, CDATA, processing
	/// instructions and a DOCTYPE are understood. It throws on anything malformed.
	/// </summary>
	sealed class Node
	{
		public string Name;
		public string Text = "";
		public readonly Dictionary<string, string> Attributes = new();
		public readonly List<Node> Children = new();

		public static Node Parse( string xml )
		{
			Node root = null;
			var open = new Stack<Node>();
			var i = 0;

			bool At( string s ) => string.CompareOrdinal( xml, i, s, 0, s.Length ) == 0;
			int Skip( string end ) => xml.IndexOf( end, i, StringComparison.Ordinal) is var at and >= 0 ? at + end.Length : throw new FormatException( "unterminated " + end );

			while ( i < xml.Length )
			{
				var lt = xml.IndexOf( '<', i );
				if ( lt < 0 ) break;
				if ( open.Count > 0 && lt > i ) open.Peek().Text += Decode( xml.Substring( i, lt - i ) );
				i = lt;

				if ( At( "<!--" ) ) { i = Skip( "-->" ); continue; }
				if ( At( "<![CDATA[" ) )
				{
					var end = xml.IndexOf( "]]>", i, StringComparison.Ordinal );
					if ( end < 0 ) throw new FormatException( "unterminated CDATA" );
					if ( open.Count > 0 ) open.Peek().Text += xml.Substring( i + 9, end - i - 9 );
					i = end + 3;
					continue;
				}
				if ( At( "<?" ) ) { i = Skip( "?>" ); continue; }
				if ( At( "<!" ) ) { i = Skip( ">" ); continue; }
				if ( At( "</" ) )
				{
					if ( open.Count == 0 ) throw new FormatException( "stray end tag" );
					open.Pop();
					i = Skip( ">" );
					continue;
				}

				// a start tag: name, attributes, then ">" or "/>"
				var j = i + 1;
				while ( j < xml.Length && !char.IsWhiteSpace( xml[j] ) && xml[j] != '>' && xml[j] != '/' ) j++;
				var node = new Node { Name = LocalName( xml.Substring( i + 1, j - i - 1 ) ) };

				while ( true )
				{
					while ( j < xml.Length && char.IsWhiteSpace( xml[j] ) ) j++;
					if ( j >= xml.Length ) throw new FormatException( "unterminated tag" );
					if ( xml[j] == '>' || xml[j] == '/' ) break;

					var eq = xml.IndexOf( '=', j );
					if ( eq < 0 ) throw new FormatException( "bad attribute" );
					var name = xml.Substring( j, eq - j ).Trim();
					var q = eq + 1;
					while ( q < xml.Length && char.IsWhiteSpace( xml[q] ) ) q++;
					if ( q >= xml.Length || (xml[q] != '"' && xml[q] != '\'') ) throw new FormatException( "bad attribute value" );
					var close = xml.IndexOf( xml[q], q + 1 );
					if ( close < 0 ) throw new FormatException( "unterminated attribute" );

					if ( name != "xmlns" && !name.StartsWith( "xmlns:" ) )
						node.Attributes[LocalName( name )] = Decode( xml.Substring( q + 1, close - q - 1 ) );
					j = close + 1;
				}

				if ( open.Count > 0 ) open.Peek().Children.Add( node );
				else if ( root is null ) root = node;
				else throw new FormatException( "several roots" );

				var selfClosing = xml[j] == '/';
				i = Skip( ">" );
				if ( !selfClosing ) open.Push( node );
			}

			return root;
		}

		static string LocalName( string name ) => name.Contains( ':' ) ? name[(name.IndexOf( ':' ) + 1)..] : name;

		static string Decode( string s )
		{
			if ( !s.Contains( '&' ) ) return s;
			s = Regex.Replace( s, "&#x([0-9a-fA-F]+);", m => char.ConvertFromUtf32( int.Parse( m.Groups[1].Value, NumberStyles.HexNumber ) ) );
			s = Regex.Replace( s, "&#(\\d+);", m => char.ConvertFromUtf32( int.Parse( m.Groups[1].Value, CultureInfo.InvariantCulture ) ) );
			return s.Replace( "&lt;", "<" ).Replace( "&gt;", ">" ).Replace( "&quot;", "\"" ).Replace( "&apos;", "'" ).Replace( "&amp;", "&" );
		}
	}

	readonly Uri manifest;
	readonly List<Rep> reps = new();
	readonly string masterText;
	readonly Dictionary<string, Rep> byPlaylist = new();
	readonly Dictionary<string, Task<string>> built = new();

	public static bool IsMpd( Uri uri )
		=> (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.AbsolutePath.EndsWith( ".mpd", StringComparison.OrdinalIgnoreCase );

	public static async Task<MpdPlaylists> LoadAsync( Uri url, CancellationToken ct )
		=> new MpdPlaylists( url, await Http.RequestStringAsync( url.ToString(), cancellationToken: ct ) );

	/// <summary> The text of one of the made up playlists - anything else comes from the network. </summary>
	public Task<string> FetchAsync( Uri uri, CancellationToken ct )
	{
		var key = uri.ToString();
		if ( key == Master.ToString() ) return Task.FromResult( masterText );
		if ( !byPlaylist.TryGetValue( key, out var rep ) ) return Http.RequestStringAsync( key, cancellationToken: ct );

		// built once; not tied to the caller's token, so a cancelled caller doesn't poison it for the next
		lock ( built ) return built.TryGetValue( key, out var task ) ? task : built[key] = BuildMedia( rep );
	}

	MpdPlaylists( Uri url, string text )
	{
		manifest = url;
		Node mpd;
		try
		{
			mpd = Node.Parse( text );
		}
		catch ( Exception )
		{
			throw new ResolveException( "That isn't a valid DASH manifest." );
		}

		if ( mpd is null || mpd.Name != "MPD" ) throw new ResolveException( "That isn't a DASH manifest." );
		if ( Attr( mpd, "type" ) == "dynamic" ) throw new ResolveException( "Live DASH streams aren't supported." );

		var periods = Kids( mpd, "Period" ).ToList();
		if ( periods.Count == 0 ) throw new ResolveException( "The DASH manifest has no content." );
		if ( periods.Count > 1 ) throw new ResolveException( "This DASH manifest has several periods (ads or chapters), which isn't supported." );

		var period = periods[0];
		Duration = ParseDuration( Attr( period, "duration" ) ?? Attr( mpd, "mediaPresentationDuration" ) );
		Title = Kids( mpd, "ProgramInformation" ).Select( p => Kids( p, "Title" ).FirstOrDefault()?.Text?.Trim() ).FirstOrDefault( t => !string.IsNullOrEmpty( t ) );

		var periodBase = WithBase( WithBase( url, mpd ), period );
		var protectedMedia = false;
		foreach ( var set in Kids( period, "AdaptationSet" ) )
		{
			var setBase = WithBase( periodBase, set );
			foreach ( var rep in Kids( set, "Representation" ) )
			{
				var mime = Attr( rep, "mimeType" ) ?? Attr( set, "mimeType" ) ?? "";
				var contentType = Attr( set, "contentType" ) ?? Attr( rep, "contentType" ) ?? "";
				var codecs = Attr( rep, "codecs" ) ?? Attr( set, "codecs" ) ?? "";
				var video = mime.StartsWith( "video" ) || contentType == "video";
				var audio = mime.StartsWith( "audio" ) || contentType == "audio";
				if ( !video && !audio ) continue; // subtitles and the like

				if ( Kids( rep, "ContentProtection" ).Any() || Kids( set, "ContentProtection" ).Any() ) { protectedMedia = true; continue; }
				if ( mime.Length > 0 && !mime.Contains( "mp4" ) ) continue; // WebM
				if ( codecs.Length > 0 && !(video ? codecs.StartsWith( "avc1" ) || codecs.StartsWith( "avc3" ) || codecs.StartsWith( "av01" ) : codecs.StartsWith( "mp4a" )) ) continue; // HEVC, E-AC-3...

				reps.Add( new Rep
				{
					Id = Attr( rep, "id" ) ?? reps.Count.ToString(),
					Bandwidth = (long)Number( rep, "bandwidth", 0 ),
					Width = (int)(Number( rep, "width", 0 ) is var w and > 0 ? w : Number( set, "width", 0 )),
					Height = (int)(Number( rep, "height", 0 ) is var h and > 0 ? h : Number( set, "height", 0 )),
					Codecs = codecs,
					Lang = Attr( set, "lang" ),
					Video = video,
					Base = WithBase( setBase, rep ),
					Element = rep,
					Set = set,
					Period = period,
					PlaylistName = $"bimp-dash-{reps.Count}.m3u8",
				} );
			}
		}

		if ( reps.Count == 0 )
			throw new ResolveException( protectedMedia ? "This DASH video is DRM protected, so it can't be played." : "This DASH manifest has nothing the engine can play (it needs MP4 with H.264 or AV1 video and AAC audio)." );

		Heights = reps.Where( r => r.Video && r.Height > 0 ).Select( r => r.Height ).Distinct().OrderByDescending( h => h ).ToList();
		Master = new Uri( url, "bimp-dash-master.m3u8" );
		foreach ( var r in reps ) byPlaylist[PlaylistUri( r ).ToString()] = r;
		masterText = BuildMaster();
	}

	Uri PlaylistUri( Rep r ) => new( manifest, r.PlaylistName );

	string BuildMaster()
	{
		var sb = new StringBuilder( "#EXTM3U\n" );
		var videos = reps.Where( r => r.Video ).ToList();

		// one audio rendition per adaptation set: its best representation, AAC-LC before HE-AAC (see AacConfig.OutputRate)
		static bool HeAac( Rep r ) => r.Codecs.StartsWith( "mp4a.40.5" ) || r.Codecs.StartsWith( "mp4a.40.29" );
		var audios = reps.Where( r => !r.Video ).GroupBy( r => r.Set )
			.Select( g => g.OrderBy( HeAac ).ThenByDescending( r => r.Bandwidth ).First() )
			.OrderBy( HeAac ).ToList();

		if ( videos.Count == 0 )
		{
			var best = audios.OrderByDescending( r => r.Bandwidth ).First();
			sb.Append( $"#EXT-X-STREAM-INF:BANDWIDTH={Math.Max( 1, best.Bandwidth )},CODECS=\"{Quote( best.Codecs )}\"\n{PlaylistUri( best )}\n" );
			return sb.ToString();
		}

		for ( var i = 0; i < audios.Count; i++ )
		{
			var a = audios[i];
			sb.Append( $"#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"{Quote( a.Lang ?? a.Id )}\",LANGUAGE=\"{Quote( a.Lang ?? "und" )}\",DEFAULT={(i == 0 ? "YES" : "NO")},URI=\"{PlaylistUri( a )}\"\n" );
		}

		foreach ( var v in videos )
		{
			var audioCodec = audios.FirstOrDefault()?.Codecs;
			var codecs = string.Join( ",", new[] { v.Codecs, audioCodec }.Where( c => !string.IsNullOrEmpty( c ) ) );
			var resolution = v.Height > 0 ? $",RESOLUTION={Math.Max( v.Width, 1 )}x{v.Height}" : "";
			sb.Append( $"#EXT-X-STREAM-INF:BANDWIDTH={Math.Max( 1, v.Bandwidth )}{resolution},CODECS=\"{Quote( codecs )}\"{(audios.Count > 0 ? ",AUDIO=\"aud\"" : "")}\n{PlaylistUri( v )}\n" );
		}

		return sb.ToString();
	}

	static string Quote( string s ) => (s ?? "").Replace( "\"", "'" ).Replace( "\n", " " );

	#region Media playlists

	sealed record Seg( Uri Url, double Duration, long? Offset, long? Length );

	sealed record Init( Uri Url, long? Offset, long? Length );

	async Task<string> BuildMedia( Rep r )
	{
		var template = Inherited( r, "SegmentTemplate" );
		var list = Inherited( r, "SegmentList" );
		var baseSegment = Inherited( r, "SegmentBase" );
		Init init = null;
		var segs = new List<Seg>();

		if ( template is not null ) init = FromTemplate( r, template, segs );
		else if ( list is not null ) init = FromList( r, list, segs );
		else if ( baseSegment is not null ) init = await FromSidx( r, baseSegment, segs );
		else throw new ResolveException( "This DASH stream doesn't say where its segments are." );

		if ( segs.Count == 0 ) throw new ResolveException( "This DASH stream has no segments." );

		var sb = new StringBuilder( "#EXTM3U\n#EXT-X-VERSION:7\n" );
		sb.Append( $"#EXT-X-TARGETDURATION:{(int)Math.Ceiling( segs.Max( s => s.Duration ) )}\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n" );
		if ( init is not null )
			sb.Append( $"#EXT-X-MAP:URI=\"{init.Url}\"{(init.Length is { } il ? $",BYTERANGE=\"{il}@{init.Offset ?? 0}\"" : "")}\n" );
		foreach ( var s in segs )
		{
			sb.Append( $"#EXTINF:{s.Duration.ToString( "0.######", CultureInfo.InvariantCulture )},\n" );
			if ( s.Length is { } len ) sb.Append( $"#EXT-X-BYTERANGE:{len}@{s.Offset ?? 0}\n" );
			sb.Append( s.Url ).Append( '\n' );
		}
		sb.Append( "#EXT-X-ENDLIST\n" );
		return sb.ToString();
	}

	/// <summary> The segment info element closest to the representation: its own, its adaptation set's, then the period's. </summary>
	static Node Inherited( Rep r, string name )
		=> Kids( r.Element, name ).FirstOrDefault() ?? Kids( r.Set, name ).FirstOrDefault() ?? Kids( r.Period, name ).FirstOrDefault();

	Init FromTemplate( Rep r, Node t, List<Seg> segs )
	{
		var timescale = Math.Max( 1, Number( t, "timescale", 1 ) );
		var startNumber = (long)Number( t, "startNumber", 1 );
		var media = Attr( t, "media" ) ?? throw new ResolveException( "This DASH stream doesn't name its segments." );
		var initTemplate = Attr( t, "initialization" );

		if ( Kids( t, "SegmentTimeline" ).FirstOrDefault() is { } timeline )
		{
			long time = 0, number = startNumber;
			foreach ( var s in Kids( timeline, "S" ) )
			{
				if ( Long( s, "t" ) is { } at ) time = at;
				var d = Long( s, "d" ) ?? throw new ResolveException( "A DASH segment has no duration." );
				var repeat = (int)(Long( s, "r" ) ?? 0);
				// -1: repeat until the end of the period
				if ( repeat < 0 ) repeat = Duration * timescale > time ? (int)Math.Ceiling( (Duration * timescale - time) / d ) - 1 : 0;

				for ( var i = 0; i <= repeat; i++ )
				{
					segs.Add( new Seg( Resolve( r.Base, Substitute( media, r, number, time ) ), d / timescale, null, null ) );
					time += d;
					number++;
				}
			}
		}
		else
		{
			var d = Number( t, "duration", 0 );
			if ( d <= 0 || Duration <= 0 ) throw new ResolveException( "This DASH stream doesn't say how long its segments are." );
			var seconds = d / timescale;
			var count = (int)Math.Ceiling( Duration / seconds - 1e-6 );
			for ( var i = 0; i < count; i++ )
				segs.Add( new Seg( Resolve( r.Base, Substitute( media, r, startNumber + i, (long)(i * d) ) ), Math.Min( seconds, Duration - i * seconds ), null, null ) );
		}

		return initTemplate is null ? null : new Init( Resolve( r.Base, Substitute( initTemplate, r, startNumber, 0 ) ), null, null );
	}

	Init FromList( Rep r, Node list, List<Seg> segs )
	{
		var timescale = Math.Max( 1, Number( list, "timescale", 1 ) );
		var seconds = Number( list, "duration", 0 ) / timescale;
		if ( seconds <= 0 ) throw new ResolveException( "This DASH stream doesn't say how long its segments are." );

		foreach ( var u in Kids( list, "SegmentURL" ) )
		{
			var url = Attr( u, "media" ) is { } m ? Resolve( r.Base, m ) : r.Base;
			var (offset, length) = Range( Attr( u, "mediaRange" ) );
			segs.Add( new Seg( url, seconds, offset, length ) );
		}

		if ( segs.Count > 0 && Duration > 0 ) segs[^1] = segs[^1] with { Duration = Math.Max( 0.1, Math.Min( seconds, Duration - seconds * (segs.Count - 1) ) ) };

		return Kids( list, "Initialization" ).FirstOrDefault() is { } i ? InitFrom( r, i ) : null;
	}

	static Init InitFrom( Rep r, Node i )
	{
		var (offset, length) = Range( Attr( i, "range" ) );
		return new Init( Attr( i, "sourceURL" ) is { } source ? Resolve( r.Base, source ) : r.Base, offset, length );
	}

	/// <summary> A single file with a segment index (on-demand profile): the index tells where each segment is. </summary>
	async Task<Init> FromSidx( Rep r, Node b, List<Seg> segs )
	{
		var (indexOffset, indexLength) = Range( Attr( b, "indexRange" ) );
		if ( indexOffset is null || indexLength is null ) throw new ResolveException( "This DASH stream has no segment index." );

		var headers = new Dictionary<string, string> { ["Range"] = $"bytes={indexOffset}-{indexOffset + indexLength - 1}" };
		var data = await Http.RequestBytesAsync( r.Base.ToString(), headers: headers );

		// the sidx box, wherever the range starts
		var at = -1;
		for ( var i = 4; i + 4 <= data.Length && at < 0; i++ )
			if ( data[i] == 's' && data[i + 1] == 'i' && data[i + 2] == 'd' && data[i + 3] == 'x' ) at = i - 4;
		if ( at < 0 ) throw new ResolveException( "Couldn't read this DASH stream's segment index." );

		uint U32( int p ) => (uint)((data[p] << 24) | (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3]);
		ulong U64( int p ) => ((ulong)U32( p ) << 32) | U32( p + 4 );

		var version = data[at + 8];
		var timescale = Math.Max( 1, U32( at + 16 ) );
		var p = at + 20;
		ulong firstOffset;
		if ( version == 0 ) { firstOffset = U32( p + 4 ); p += 8; }
		else { firstOffset = U64( p + 8 ); p += 16; }
		var count = (data[p + 2] << 8) | data[p + 3];
		p += 4;

		// the first byte after the sidx box is where "first_offset" counts from
		var position = (long)indexOffset + at + U32( at ) + (long)firstOffset;
		for ( var i = 0; i < count && p + 12 <= data.Length; i++, p += 12 )
		{
			var word = U32( p );
			if ( (word & 0x80000000) != 0 ) throw new ResolveException( "This DASH stream's segment index is nested, which isn't supported." );
			var size = word & 0x7FFFFFFF;
			segs.Add( new Seg( r.Base, U32( p + 4 ) / (double)timescale, position, size ) );
			position += size;
		}

		var init = Kids( b, "Initialization" ).FirstOrDefault();
		return init is not null && Attr( init, "range" ) is not null ? InitFrom( r, init ) : new Init( r.Base, 0, indexOffset );
	}

	#endregion

	#region XML and number helpers

	static IEnumerable<Node> Kids( Node e, string name ) => e?.Children.Where( x => x.Name == name ) ?? Enumerable.Empty<Node>();

	static string Attr( Node e, string name ) => e is not null && e.Attributes.TryGetValue( name, out var value ) ? value : null;

	static double Number( Node e, string name, double fallback )
		=> double.TryParse( Attr( e, name ), NumberStyles.Float, CultureInfo.InvariantCulture, out var n ) ? n : fallback;

	static long? Long( Node e, string name )
		=> long.TryParse( Attr( e, name ), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n ) ? n : null;

	/// <summary> The element's BaseURL (if any) resolved against the parent's. </summary>
	static Uri WithBase( Uri parent, Node e )
		=> Kids( e, "BaseURL" ).FirstOrDefault()?.Text?.Trim() is { Length: > 0 } b ? Resolve( parent, b ) : parent;

	static Uri Resolve( Uri parent, string relative ) => new( parent, relative );

	/// <summary> "first-last" (inclusive) as an offset and a length. </summary>
	static (long? offset, long? length) Range( string range )
	{
		var p = range?.Split( '-' );
		if ( p is { Length: 2 } && long.TryParse( p[0], out var a ) && long.TryParse( p[1], out var b ) && b >= a ) return (a, b - a + 1);
		return (null, null);
	}

	// $Number$, $Number%05d$, $Time$, $Bandwidth$, $RepresentationID$; $$ is a dollar sign
	static readonly Regex Token = new( "\\$(RepresentationID|Number|Bandwidth|Time)?(?:%0(\\d+)d)?\\$", RegexOptions.Compiled );

	static string Substitute( string template, Rep r, long number, long time ) => Token.Replace( template, m =>
	{
		if ( m.Groups[1].Length == 0 ) return "$";
		var value = m.Groups[1].Value switch
		{
			"RepresentationID" => r.Id,
			"Number" => number.ToString( CultureInfo.InvariantCulture ),
			"Bandwidth" => r.Bandwidth.ToString( CultureInfo.InvariantCulture ),
			_ => time.ToString( CultureInfo.InvariantCulture ),
		};
		return m.Groups[2].Success && long.TryParse( value, out var n ) ? n.ToString( "D" + m.Groups[2].Value, CultureInfo.InvariantCulture ) : value;
	} );

	/// <summary> ISO 8601 "PT1H2M3.5S" in seconds, 0 if it isn't one. </summary>
	static double ParseDuration( string iso )
	{
		if ( string.IsNullOrEmpty( iso ) ) return 0;
		var m = Regex.Match( iso, "^P(?:(\\d+(?:\\.\\d+)?)D)?(?:T(?:(\\d+(?:\\.\\d+)?)H)?(?:(\\d+(?:\\.\\d+)?)M)?(?:(\\d+(?:\\.\\d+)?)S)?)?$" );
		if ( !m.Success ) return 0;

		double Part( int g ) => m.Groups[g].Success ? double.Parse( m.Groups[g].Value, CultureInfo.InvariantCulture ) : 0;
		return Part( 1 ) * 86400 + Part( 2 ) * 3600 + Part( 3 ) * 60 + Part( 4 );
	}

	#endregion
}
