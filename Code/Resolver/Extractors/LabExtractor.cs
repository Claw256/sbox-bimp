using System.Threading;

namespace Bimp.Resolver.Extractors;

/// <summary>
/// [probe] Local test media for the merged (YouTube) path: http://127.0.0.1:8443/lab/NAME plays NAME_v.webm and
/// NAME_a.webm from a range server on loopback, merged like a YouTube video.
/// </summary>
public sealed class LabExtractor : IExtractor
{
	public string Key => "lab";

	public bool CanHandle( Uri url ) => url.IsLoopback && url.AbsolutePath.StartsWith( "/lab/" );

	public async Task<MediaInfo> GetInfoAsync( Uri url, bool audioOnly, CancellationToken ct )
	{
		var id = $"{url.Port}/{url.AbsolutePath[5..]}";
		var duration = float.TryParse( url.Query.TrimStart( '?' ), out var d ) ? d : 0;
		await Task.CompletedTask;
		return new MediaInfo { Extractor = Key, Id = id, Title = $"lab {id}", Duration = duration, AudioOnly = audioOnly, Merged = true };
	}

	public async Task<StreamPlan> GetStreamsAsync( string id, StreamRequest request, CancellationToken ct )
	{
		var port = id[..id.IndexOf( '/' )];
		var name = id[(id.IndexOf( '/' ) + 1)..];
		async Task<StreamFile> File( string suffix )
		{
			var url = $"http://127.0.0.1:{port}/{name}_{suffix}.webm";
			using var head = await Http.RequestAsync( url, "HEAD", cancellationToken: ct );
			return new StreamFile { Url = url, Size = head.Content.Headers.ContentLength ?? 0, FormatId = suffix };
		}
		return new StreamPlan { Kind = request.AudioOnly ? StreamKind.Audio : StreamKind.Merge, Video = request.AudioOnly ? null : await File( "v" ), Audio = await File( "a" ) };
	}
}
