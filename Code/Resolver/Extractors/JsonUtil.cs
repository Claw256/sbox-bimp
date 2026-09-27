using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bimp.Resolver.Extractors;

/// <summary>
/// Forgiving accessors for JSON from sites that mix numbers and numeric strings.
/// </summary>
internal static class JsonUtil
{
	public static string Str( this JsonNode node )
	{
		if ( node is not JsonValue v ) return null;
		return v.GetValueKind() switch
		{
			JsonValueKind.String => v.GetValue<string>(),
			JsonValueKind.Number => v.ToJsonString(),
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			_ => null,
		};
	}

	public static long Long( this JsonNode node )
	{
		var s = node.Str();
		if ( s is null ) return 0;
		return double.TryParse( s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d ) ? (long)d : 0;
	}

	public static int Int( this JsonNode node ) => (int)node.Long();

	public static float Float( this JsonNode node )
	{
		var s = node.Str();
		return s is not null && float.TryParse( s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f ) ? f : 0;
	}

	public static bool Bool( this JsonNode node ) => node.Str() == "true";

	public static IEnumerable<JsonNode> Items( this JsonNode node )
		=> node is JsonArray a ? a.Where( x => x is not null ) : Enumerable.Empty<JsonNode>();

	/// <summary> Depth first search for the first object property with this name. </summary>
	public static JsonNode Find( this JsonNode node, string name )
	{
		switch ( node )
		{
			case JsonObject o:
				if ( o.TryGetPropertyValue( name, out var direct ) && direct is not null ) return direct;
				foreach ( var (_, child) in o )
				{
					var found = child?.Find( name );
					if ( found is not null ) return found;
				}
				break;
			case JsonArray a:
				foreach ( var child in a )
				{
					var found = child?.Find( name );
					if ( found is not null ) return found;
				}
				break;
		}
		return null;
	}
}
