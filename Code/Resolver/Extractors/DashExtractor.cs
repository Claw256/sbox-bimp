using System.Threading;
using Bimp.Resolver.Live;

namespace Bimp.Resolver.Extractors;

/// <summary>
/// Any .mpd link: a finished (static) DASH manifest plays as a seekable video, remuxed from a start time like a finished
/// HLS playlist (see <see cref="MpdPlaylists"/>, which turns the manifest into playlists <see cref="HlsReader"/> reads).
/// </summary>
public sealed class DashExtractor : IExtractor
{
	public string Key => "dash";

	public bool CanHandle( Uri url ) => MpdPlaylists.IsMpd( url );

	public async Task<MediaInfo> GetInfoAsync( Uri url, bool audioOnly, CancellationToken ct )
	{
		var mpd = await MpdPlaylists.LoadAsync( url, ct );
		var name = Uri.UnescapeDataString( System.IO.Path.GetFileNameWithoutExtension( url.AbsolutePath ) );

		return new MediaInfo
		{
			Extractor = Key,
			Id = url.ToString(),
			Title = !string.IsNullOrWhiteSpace( mpd.Title ) ? mpd.Title : name is "manifest" or "index" or "stream" or "" ? url.Host : $"{url.Host} - {name}",
			Duration = (float)mpd.Duration,
			AudioOnly = audioOnly || mpd.Heights.Count == 0,
			Merged = true,
			Qualities = audioOnly || mpd.Heights.Count < 2 ? new List<int>() : mpd.Heights,
		};
	}

	public Task<StreamPlan> GetStreamsAsync( string id, StreamRequest request, CancellationToken ct )
		=> Task.FromResult( new StreamPlan { Kind = StreamKind.Hls, DirectUrl = id } );
}
