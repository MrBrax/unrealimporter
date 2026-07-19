using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Editor.UnrealImporter;

/// <summary>
/// Turns a manifest "scene" section (raw Unreal placements + lights) into an s&amp;box
/// .prefab: one child GameObject per placement with a ModelRenderer pointing at the
/// imported vmdl, plus Point/Spot/Directional lights.
///
/// Coordinate conversion (Unreal cm left-handed X-fwd/Y-right/Z-up -> Source inch
/// right-handed X-fwd/Y-left/Z-up) mirrors across the XZ plane:
///   position (x, -y, z) / 2.54      quaternion mirror: (-x, y, -z, w)
///
/// The FBX mesh path adds a twist: UE's exporter negates Y and Source 2's importer
/// rotates 90°, so an imported mesh's local axes are UE's with X and Y SWAPPED
/// (verified against UE bounding boxes: sbox (x,y,z) = ue (y,x,z)/2.54). A model
/// placement must compensate: R' = S·R·M, i.e. the mirrored quaternion post-multiplied
/// by yaw -90, and non-uniform scale swaps x/y. Lights carry no mesh, so they use the
/// plain mirror.
/// </summary>
public static class ScenePrefabBuilder
{
	const float UeToInch = 1f / 2.54f;

	/// <summary>
	/// Write &lt;scene name&gt;.prefab under outputRoot. modelsByGamePath maps the manifest's
	/// /Game mesh paths to imported vmdl content paths. Returns the prefab's absolute path.
	/// </summary>
	public static string Build( ManifestScene scene, IReadOnlyDictionary<string, string> modelsByGamePath, string outputRoot, List<string> warnings )
	{
		var children = new JsonArray();
		var missingMeshes = new HashSet<string>();

		foreach ( var p in scene.Placements ?? new() )
		{
			if ( string.IsNullOrEmpty( p.Mesh ) || !modelsByGamePath.TryGetValue( p.Mesh, out var vmdl ) )
			{
				if ( p.Mesh is not null )
					missingMeshes.Add( p.Mesh );
				continue;
			}

			var go = GameObjectNode( p.Name, p.Pos, p.Rot, p.Scale, isMesh: true );
			go["Components"] = new JsonArray( ComponentNode( "Sandbox.ModelRenderer", new()
			{
				["Model"] = vmdl,
				["Tint"] = "1,1,1,1",
				["RenderType"] = "On",
			} ) );
			children.Add( go );
		}

		foreach ( var l in scene.Lights ?? new() )
		{
			var node = LightNode( l );
			if ( node is not null )
				children.Add( node );
		}

		foreach ( var m in missingMeshes )
			warnings.Add( $"scene: no imported model for {m}, its placements were skipped." );

		var root = GameObjectNode( Sanitize( scene.Name ), null, null, null );
		root["Children"] = children;

		var prefab = new JsonObject
		{
			["RootObject"] = root,
			["ResourceVersion"] = 2,
			["ShowInMenu"] = false,
			["MenuPath"] = null,
			["MenuIcon"] = null,
			["DontBreakAsTemplate"] = false,
			["__references"] = new JsonArray(),
			["__version"] = 2,
		};

		var path = Path.Combine( outputRoot, Sanitize( scene.Name ) + ".prefab" );
		File.WriteAllText( path, prefab.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );
		return path;
	}

	static JsonObject GameObjectNode( string name, float[] uePos, float[] ueRot, float[] ueScale, bool isMesh = false )
	{
		var scale = ueScale ?? new float[] { 1, 1, 1 };
		if ( isMesh && scale.Length >= 3 )
			scale = new[] { scale[1], scale[0], scale[2] };   // mesh local axes are swapped

		return new JsonObject
		{
			["__guid"] = Guid.NewGuid().ToString(),
			["__version"] = 2,
			["Flags"] = 0,
			["Name"] = string.IsNullOrEmpty( name ) ? "unnamed" : name,
			["Position"] = Vec3( ConvertPosition( uePos ) ),
			["Rotation"] = Quat( ConvertRotation( ueRot, isMesh ) ),
			["Scale"] = Vec3( scale ),
			["Enabled"] = true,
		};
	}

	static JsonObject ComponentNode( string type, JsonObject properties )
	{
		var node = new JsonObject
		{
			["__type"] = type,
			["__guid"] = Guid.NewGuid().ToString(),
			["__enabled"] = true,
			["Flags"] = 0,
		};
		foreach ( var kv in properties )
			node[kv.Key] = kv.Value?.DeepClone();

		return node;
	}

	static JsonObject LightNode( ManifestLight l )
	{
		var (type, props) = l.Type switch
		{
			"point" => ("Sandbox.PointLight", new JsonObject
			{
				["LightColor"] = ColorStr( l ),
				["Radius"] = Round( (l.Radius ?? 1000f) * UeToInch ),
			}),
			"spot" => ("Sandbox.SpotLight", new JsonObject
			{
				["LightColor"] = ColorStr( l ),
				["Radius"] = Round( (l.Radius ?? 1000f) * UeToInch ),
				["ConeInner"] = Round( l.InnerCone ?? 30f ),
				["ConeOuter"] = Round( l.OuterCone ?? 45f ),
			}),
			"directional" => ("Sandbox.DirectionalLight", new JsonObject
			{
				["LightColor"] = ColorStr( l ),
				["Shadows"] = true,
			}),
			_ => (null, null),
		};

		if ( type is null )
			return null;

		var go = GameObjectNode( l.Name ?? l.Type, l.Pos, l.Rot, null );
		go["Components"] = new JsonArray( ComponentNode( type, props ) );
		return go;
	}

	static float[] ConvertPosition( float[] p )
	{
		if ( p is null || p.Length < 3 )
			return new float[] { 0, 0, 0 };

		return new[] { p[0] * UeToInch, -p[1] * UeToInch, p[2] * UeToInch };
	}

	// Yaw -90: compensates the X<->Y swap the FBX mesh pipeline bakes into mesh space.
	static readonly float[] MeshAxisFix = { 0, 0, -0.70710678f, 0.70710678f };

	static float[] ConvertRotation( float[] q, bool isMesh )
	{
		if ( q is null || q.Length < 4 )
			q = new float[] { 0, 0, 0, 1 };

		var mirrored = new[] { -q[0], q[1], -q[2], q[3] };
		return isMesh ? MulQuat( mirrored, MeshAxisFix ) : mirrored;
	}

	/// <summary>Hamilton product a*b (xyzw): rotation b in local space followed by a.</summary>
	static float[] MulQuat( float[] a, float[] b )
	{
		return new[]
		{
			a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
			a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
			a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
			a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
		};
	}

	/// <summary>
	/// A ~150 cd source (decent interior bulb) maps to HDR magnitude 1.0 - the scale
	/// hand-placed s&amp;box lights sit at (~0.35-2 in this project's scenes).
	/// </summary>
	const float RefCandela = 150f;

	/// <summary>
	/// Legacy UNITLESS lights aren't physical: UE4-scale authoring puts a strong lamp
	/// around 1000-5000. Converting them through UE's official unitless-&gt;candela factor
	/// (16/10000) lands at fractions of a candela and everything goes black, so they get
	/// their own perceptual reference instead.
	/// </summary>
	const float RefUnitless = 800f;

	/// <summary>
	/// s&amp;box lights carry brightness in LightColor's HDR magnitude. Scale the Unreal
	/// chroma by intensity: candela for physically-united point/spot lights, a UE4-scale
	/// heuristic for UNITLESS ones, lux for directional. Raw UE intensities are
	/// unit-dependent - comparing them without units is what made imports blinding.
	/// sqrt compresses the huge dynamic range of authored UE values.
	/// </summary>
	static string ColorStr( ManifestLight l )
	{
		var c = l.Color is { Length: >= 3 } ? l.Color : new float[] { 1, 1, 1 };

		float brightness;
		if ( l.Type == "directional" && l.Intensity is > 0 )
			brightness = Math.Clamp( MathF.Sqrt( l.Intensity.Value / 5f ), 0.5f, 3f );   // lux; UE legacy suns sit ~2-15
		else if ( l.Units?.StartsWith( "UNITLESS" ) == true && l.Intensity is > 0 )
			brightness = Math.Clamp( MathF.Sqrt( l.Intensity.Value / RefUnitless ), 0.05f, 3f );
		else if ( l.Candela is > 0 )
			brightness = Math.Clamp( MathF.Sqrt( l.Candela.Value / RefCandela ), 0.05f, 3f );
		else if ( l.Intensity is > 0 )
			brightness = Math.Clamp( MathF.Sqrt( l.Intensity.Value / 8f ), 0.25f, 4f );  // old manifests: unit unknown
		else
			brightness = 1f;

		return $"{F( c[0] * brightness )},{F( c[1] * brightness )},{F( c[2] * brightness )},1";
	}

	static string Vec3( float[] v ) => $"{F( v[0] )},{F( v[1] )},{F( v[2] )}";
	static string Quat( float[] q ) => $"{F( q[0] )},{F( q[1] )},{F( q[2] )},{F( q[3] )}";
	static float Round( float v ) => (float)Math.Round( v, 3 );
	static string F( float v ) => v.ToString( "0.######", CultureInfo.InvariantCulture );

	static string Sanitize( string s )
	{
		if ( string.IsNullOrEmpty( s ) )
			return "unnamed_scene";

		var chars = s.ToLowerInvariant().ToCharArray();
		for ( int i = 0; i < chars.Length; i++ )
		{
			var ch = chars[i];
			if ( ch is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_') )
				chars[i] = '_';
		}
		return new string( chars );
	}
}
