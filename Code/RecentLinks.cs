namespace Bimp;

/// <summary>
/// Links this client has seen played on any media player, newest first, saved to <see cref="FileSystem.Data"/> so they
/// survive restarts. The remote's "Recent" list plays or queues one again - the way in for a controller, which can't
/// type or paste a link (games can't read the clipboard, only a focused text box can take Ctrl+V).
/// </summary>
public static class RecentLinks
{
	public sealed class Entry
	{
		/// <summary> The link as it was entered (page url, not the resolved stream). </summary>
		public string Url { get; set; }
		public string Title { get; set; }

		/// <summary> Seconds, 0 if unknown. </summary>
		public float Duration { get; set; }
		public bool IsLive { get; set; }
	}

	const int MaxEntries = 30;
	const string FilePath = "bimp/recent.json";

	static List<Entry> entries;

	/// <summary> The PlayId and duration last recorded for each player. </summary>
	static readonly Dictionary<Guid, (int playId, float duration)> seen = new();

	/// <summary> Changes whenever the list does (for UI rebuilds). </summary>
	public static int Version { get; private set; }

	/// <summary> Newest first. </summary>
	public static IReadOnlyList<Entry> All => Load();

	/// <summary>
	/// Call every frame for each player: records what it's playing when it starts something (or learns its duration).
	/// </summary>
	public static void Saw( MediaPlayer player )
	{
		if ( !player.HasMedia || string.IsNullOrWhiteSpace( player.CurrentUrl ) ) return;

		var now = (player.PlayId, player.MediaDuration);
		if ( seen.TryGetValue( player.Id, out var last ) && last == now ) return;
		seen[player.Id] = now;

		var list = Load();
		list.RemoveAll( e => string.Equals( e.Url, player.CurrentUrl, StringComparison.OrdinalIgnoreCase ) );
		list.Insert( 0, new Entry
		{
			Url = player.CurrentUrl,
			Title = string.IsNullOrWhiteSpace( player.Title ) ? player.CurrentUrl : player.Title,
			Duration = player.MediaDuration,
			IsLive = player.IsLive,
		} );
		if ( list.Count > MaxEntries ) list.RemoveRange( MaxEntries, list.Count - MaxEntries );

		Version++;
		Save();
	}

	/// <summary> Forget one link. </summary>
	public static void Remove( string url )
	{
		if ( Load().RemoveAll( e => e.Url == url ) == 0 ) return;
		Version++;
		Save();
	}

	static List<Entry> Load()
	{
		if ( entries is not null ) return entries;
		try
		{
			entries = FileSystem.Data.FileExists( FilePath ) ? FileSystem.Data.ReadJson<List<Entry>>( FilePath ) : null;
		}
		catch ( Exception e )
		{
			Log.Warning( $"[bimp] couldn't read the recent links: {e.Message}" );
		}
		entries ??= new();
		entries.RemoveAll( e => string.IsNullOrWhiteSpace( e?.Url ) );
		return entries;
	}

	static void Save()
	{
		try
		{
			FileSystem.Data.CreateDirectory( "bimp" );
			FileSystem.Data.WriteJson( FilePath, entries );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[bimp] couldn't save the recent links: {e.Message}" );
		}
	}
}
