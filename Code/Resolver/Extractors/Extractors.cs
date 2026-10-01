namespace Bimp.Resolver.Extractors;

/// <summary>
/// Every site the native resolver understands.
/// </summary>
public static class ExtractorRegistry
{
	// built on each call, not kept in a static: a hotload keeps statics, so new extractors never showed up (their caches
	// are statics of their own)
	public static IReadOnlyList<IExtractor> All => new IExtractor[]
	{
		new YouTubeExtractor(),
		new StreamableExtractor(),
		new VimeoExtractor(),
		new SoundCloudExtractor(),
		new TwitchExtractor(),
		new KickExtractor(),
		new XExtractor(),
		new ArchiveExtractor(),
		new BandcampExtractor(),
		new DailymotionExtractor(),
		new HlsExtractor(), // any .m3u8 link
		new DashExtractor(), // any .mpd link
		new LabExtractor(), // [probe] loopback test media
	};

	public static IExtractor For( Uri url ) => All.FirstOrDefault( e => e.CanHandle( url ) );

	public static IExtractor ByKey( string key ) => All.FirstOrDefault( e => e.Key == key );
}
