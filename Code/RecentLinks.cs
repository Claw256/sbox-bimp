using System.Runtime.CompilerServices;

namespace Bimp;

/// <summary>
/// Links played on the media players of one scene, newest first. Kept in memory per scene instance and never saved,
/// so a different server or scene (or a later session) starts empty and never shows links from somewhere else. The
/// remote's "Recent" list plays or queues one again - the way in for a controller, which can't type or paste a link
/// (games can't read the clipboard, only a focused text box can take Ctrl+V).
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

	/// <summary> Older versions saved the list here, across every server. It's removed once so old links don't linger. </summary>
	const string LegacyFilePath = "bimp/recent.json";

	static readonly ConditionalWeakTable<Scene, List<Entry>> byScene = new();
	static readonly IReadOnlyList<Entry> none = new List<Entry>();
	static bool legacyCleaned;

	/// <summary> The PlayId and duration last recorded for each player. </summary>
	static readonly Dictionary<Guid, (int playId, float duration)> seen = new();

	/// <summary> Changes whenever a list does (for UI rebuilds). </summary>
	public static int Version { get; private set; }

	/// <summary> The links played in this scene, newest first. Empty if nothing has been played in it. </summary>
	public static IReadOnlyList<Entry> Get( Scene scene )
		=> scene is not null && byScene.TryGetValue( scene, out var list ) ? list : none;

	/// <summary>
	/// Call every frame for each player: records what it's playing when it starts something (or learns its duration).
	/// </summary>
	public static void Saw( MediaPlayer player )
	{
		if ( !player.HasMedia || string.IsNullOrWhiteSpace( player.CurrentUrl ) ) return;

		var scene = player.Scene;
		if ( scene is null ) return;

		var now = (player.PlayId, player.MediaDuration);
		if ( seen.TryGetValue( player.Id, out var last ) && last == now ) return;
		seen[player.Id] = now;

		DeleteLegacyFile();

		var list = byScene.GetOrCreateValue( scene );
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
	}

	/// <summary> Forget one link in this scene. </summary>
	public static void Remove( Scene scene, string url )
	{
		if ( scene is null || !byScene.TryGetValue( scene, out var list ) ) return;
		if ( list.RemoveAll( e => e.Url == url ) == 0 ) return;
		Version++;
	}

	static void DeleteLegacyFile()
	{
		if ( legacyCleaned ) return;
		legacyCleaned = true;
		try
		{
			if ( FileSystem.Data.FileExists( LegacyFilePath ) ) FileSystem.Data.DeleteFile( LegacyFilePath );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[bimp] couldn't remove the old recent links file: {e.Message}" );
		}
	}
}
