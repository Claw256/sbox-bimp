using System.Text.Json.Nodes;

namespace Bimp.Resolver;

/// <summary>
/// An InnerTube (YouTube's internal API) client identity. Different clients get different format lists;
/// we only use ones whose formats come with a plain url - no signature cipher, so no JavaScript is needed.
/// </summary>
public sealed class InnerTubeClient
{
	/// <summary> Short name used in the bimp_yt_clients convar. </summary>
	public string Key { get; init; }

	/// <summary> context.client.clientName </summary>
	public string Name { get; init; }

	public string Version { get; init; }

	/// <summary> X-YouTube-Client-Name </summary>
	public int Id { get; init; }

	/// <summary> Extra context.client fields (device, os, userAgent...). </summary>
	public Dictionary<string, object> Extra { get; init; } = new();

	public JsonObject BuildContext()
	{
		var client = new JsonObject
		{
			["clientName"] = Name,
			["clientVersion"] = Version,
			["hl"] = "en",
			["gl"] = "US",
			["timeZone"] = "UTC",
			["utcOffsetMinutes"] = 0,
		};

		foreach ( var (k, v) in Extra )
		{
			client[k] = v switch
			{
				int i => JsonValue.Create( i ),
				string s => JsonValue.Create( s ),
				_ => JsonValue.Create( v.ToString() ),
			};
		}

		return new JsonObject { ["client"] = client };
	}
}

/// <summary>
/// The known clients, in the default order they're tried. YouTube changes which clients work every so often -
/// the order can be overridden with the bimp_yt_clients convar without touching code.
/// </summary>
public static class InnerTubeClients
{
	public static readonly InnerTubeClient AndroidVr = new()
	{
		Key = "android_vr",
		Name = "ANDROID_VR",
		Version = "1.65.10",
		Id = 28,
		Extra = new()
		{
			["deviceMake"] = "Oculus",
			["deviceModel"] = "Quest 3",
			["androidSdkVersion"] = 32,
			["osName"] = "Android",
			["osVersion"] = "12L",
			["userAgent"] = "com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip",
		},
	};

	public static readonly InnerTubeClient Ios = new()
	{
		Key = "ios",
		Name = "IOS",
		Version = "20.10.4",
		Id = 5,
		Extra = new()
		{
			["deviceMake"] = "Apple",
			["deviceModel"] = "iPhone16,2",
			["osName"] = "iPhone",
			["osVersion"] = "18.3.2.22D82",
			["userAgent"] = "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)",
		},
	};

	public static readonly InnerTubeClient VisionOs = new()
	{
		Key = "visionos",
		Name = "VISIONOS",
		Version = "0.1",
		Id = 101,
		Extra = new()
		{
			["deviceMake"] = "Apple",
			["deviceModel"] = "RealityDevice14,1",
			["osName"] = "visionOS",
			["osVersion"] = "1.3.21O771",
		},
	};

	public static readonly InnerTubeClient Android = new()
	{
		Key = "android",
		Name = "ANDROID",
		Version = "20.10.38",
		Id = 3,
		Extra = new()
		{
			["androidSdkVersion"] = 30,
			["osName"] = "Android",
			["osVersion"] = "11",
			["userAgent"] = "com.google.android.youtube/20.10.38 (Linux; U; Android 11) gzip",
		},
	};

	/// <summary>
	/// The website's client. Not in <see cref="All"/> (its formats need the signature cipher): only used for the "next"
	/// endpoint, where it answers with a playlist's whole panel, which the mobile clients don't.
	/// </summary>
	public static readonly InnerTubeClient Web = new()
	{
		Key = "web",
		Name = "WEB",
		Version = "2.20250312.04.00",
		Id = 1,
	};

	public static readonly IReadOnlyList<InnerTubeClient> All = new[] { VisionOs, AndroidVr, Ios, Android };

	public const string DefaultOrder = "visionos,android_vr,ios";

	/// <summary>
	/// The clients to try, in order, from a comma separated list of keys. Unknown keys are ignored.
	/// </summary>
	public static List<InnerTubeClient> Ordered( string keys )
	{
		if ( string.IsNullOrWhiteSpace( keys ) ) keys = DefaultOrder;

		var list = keys.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
			.Select( k => All.FirstOrDefault( c => string.Equals( c.Key, k, StringComparison.OrdinalIgnoreCase ) ) )
			.Where( c => c is not null )
			.Distinct()
			.ToList();

		return list.Count > 0 ? list : Ordered( DefaultOrder );
	}
}
