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
	/// /Game mesh paths to imported vmdl content paths; mirroredByGamePath the variants for
	/// mirrored (odd-negative-scale) placements. Returns the prefab's absolute path.
	/// </summary>
	public static string Build( ManifestScene scene, IReadOnlyDictionary<string, string> modelsByGamePath, string outputRoot, List<string> warnings,
		IReadOnlyDictionary<string, string> mirroredByGamePath = null, float lightScale = 1f )
	{
		var children = new JsonArray();
		var missingMeshes = new HashSet<string>();
		var missingMirrors = new HashSet<string>();

		foreach ( var p in scene.Placements ?? new() )
		{
			if ( string.IsNullOrEmpty( p.Mesh ) || !modelsByGamePath.TryGetValue( p.Mesh, out var vmdl ) )
			{
				if ( p.Mesh is not null )
					missingMeshes.Add( p.Mesh );
				continue;
			}

			// True mirrors (odd negative axes) swap to the mirrored model variant.
			if ( p.Scale is { Length: >= 3 } && NegativeCount( p.Scale ) % 2 == 1 )
			{
				if ( mirroredByGamePath is not null && mirroredByGamePath.TryGetValue( p.Mesh, out var mirrored ) )
					vmdl = mirrored;
				else
					missingMirrors.Add( p.Mesh );
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

		foreach ( var m in missingMirrors )
			warnings.Add( $"scene: no mirrored model for {m}, its flipped placements will render inside-out." );

		foreach ( var l in scene.Lights ?? new() )
		{
			var node = LightNode( l, lightScale );
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

		var rot = ConvertRotation( ueRot, isMesh );
		(rot, scale) = ResolveNegativeScale( rot, scale );

		return new JsonObject
		{
			["__guid"] = Guid.NewGuid().ToString(),
			["__version"] = 2,
			["Flags"] = 0,
			["Name"] = string.IsNullOrEmpty( name ) ? "unnamed" : name,
			["Position"] = Vec3( ConvertPosition( uePos ) ),
			["Rotation"] = Quat( rot ),
			["Scale"] = Vec3( scale ),
			["Enabled"] = true,
		};
	}

	static int NegativeCount( float[] s ) => (s[0] < 0 ? 1 : 0) + (s[1] < 0 ? 1 : 0) + (s[2] < 0 ? 1 : 0);

	// 180° rotations about local X / Y / Z, quaternion xyzw.
	static readonly float[][] Rot180 = { new float[] { 1, 0, 0, 0 }, new float[] { 0, 1, 0, 0 }, new float[] { 0, 0, 1, 0 } };

	/// <summary>
	/// s&amp;box doesn't flip triangle winding for negative GameObject scale, so negative axes
	/// must not reach the prefab. diag(-a,-b,c) == rot180_z * diag(a,b,c): an EVEN number of
	/// negative axes folds into a 180° local rotation (about the remaining positive axis).
	/// An ODD count is a true mirror, served by the model's mirrored variant M = diag(-1,1,1)
	/// (ScaleAndMirror across local X). The sign pattern factors as sign = M * Q with Q a
	/// 180° rotation (sign matrices commute): negative x -> Q = identity; negative y ->
	/// rot180_z; negative z -> rot180_y; all three -> rot180_x. Callers pick the mirrored
	/// model for odd counts.
	/// </summary>
	static (float[] rot, float[] scale) ResolveNegativeScale( float[] rot, float[] scale )
	{
		if ( scale.Length < 3 || NegativeCount( scale ) == 0 )
			return (rot, scale);

		int negatives = NegativeCount( scale );
		int axis = -1;
		if ( negatives == 2 )
		{
			axis = Array.FindIndex( scale, v => v >= 0 );      // rotate about the positive axis
		}
		else if ( negatives == 1 )
		{
			// X-mirrored model: sign pattern (-,+,+) is the model itself; (+,-,+) needs
			// rot180 about Z on top of it; (+,+,-) rot180 about Y.
			int neg = Array.FindIndex( scale, v => v < 0 );
			axis = neg switch { 1 => 2, 2 => 1, _ => -1 };
		}
		else if ( negatives == 3 )
		{
			axis = 0;                                          // (-,-,-) = M * rot180_x
		}

		if ( axis >= 0 )
			rot = MulQuat( rot, Rot180[axis] );

		return (rot, new[] { Math.Abs( scale[0] ), Math.Abs( scale[1] ), Math.Abs( scale[2] ) });
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

	static JsonObject LightNode( ManifestLight l, float lightScale )
	{
		var (type, props) = l.Type switch
		{
			"point" => ("Sandbox.PointLight", new JsonObject
			{
				["LightColor"] = ColorStr( l, lightScale ),
				["Radius"] = Round( (l.Radius ?? 1000f) * UeToInch ),
			}),
			"spot" => ("Sandbox.SpotLight", new JsonObject
			{
				["LightColor"] = ColorStr( l, lightScale ),
				["Radius"] = Round( (l.Radius ?? 1000f) * UeToInch ),
				["ConeInner"] = Round( l.InnerCone ?? 30f ),
				["ConeOuter"] = Round( l.OuterCone ?? 45f ),
			}),
			"directional" => ("Sandbox.DirectionalLight", new JsonObject
			{
				["LightColor"] = ColorStr( l, lightScale ),
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
	/// A ~1000 cd source (strong ceiling fixture) maps to HDR magnitude 1.0. Calibrated
	/// visually against the CCA subway terminal: UE relies on auto-exposure to pull
	/// physically-lit interiors (dozens of overlapping 500-1000 cd lights) down to
	/// comfortable levels, s&amp;box doesn't - mapping generously blows every surface to
	/// white. Hand-placed lights in this project sit at ~0.35-2 HDR magnitude.
	/// </summary>
	const float RefCandela = 1000f;

	/// <summary>
	/// Legacy UNITLESS lights aren't physical: UE4-scale authoring puts a strong lamp
	/// around 1000-5000. Converting them through UE's official unitless-&gt;candela factor
	/// (16/10000) lands at fractions of a candela and everything goes black, so they get
	/// their own perceptual reference instead (calibrated with the same 0.4 factor as
	/// the candela path).
	/// </summary>
	const float RefUnitless = 5000f;

	/// <summary>
	/// s&amp;box lights carry brightness in LightColor's HDR magnitude. Scale the Unreal
	/// chroma by intensity: candela for physically-united point/spot lights, a UE4-scale
	/// heuristic for UNITLESS ones, lux for directional. Raw UE intensities are
	/// unit-dependent - comparing them without units is what made imports blinding.
	/// sqrt compresses the huge dynamic range of authored UE values.
	/// </summary>
	static string ColorStr( ManifestLight l, float lightScale )
	{
		var c = l.Color is { Length: >= 3 } ? l.Color : new float[] { 1, 1, 1 };

		float brightness;
		if ( l.Type == "directional" && l.Intensity is > 0 )
			brightness = Math.Clamp( MathF.Sqrt( l.Intensity.Value / 5f ), 0.5f, 2.5f );   // lux; UE legacy suns sit ~2-15
		else if ( l.Units?.StartsWith( "UNITLESS" ) == true && l.Intensity is > 0 )
			brightness = Math.Clamp( MathF.Sqrt( l.Intensity.Value / RefUnitless ), 0.05f, 2f );
		else if ( l.Candela is > 0 )
			brightness = Math.Clamp( MathF.Sqrt( l.Candela.Value / RefCandela ), 0.05f, 2f );
		else if ( l.Intensity is > 0 )
			brightness = Math.Clamp( MathF.Sqrt( l.Intensity.Value / 8f ), 0.25f, 4f );  // old manifests: unit unknown
		else
			brightness = 1f;

		brightness = Math.Clamp( brightness * lightScale, 0.02f, 4f );

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
