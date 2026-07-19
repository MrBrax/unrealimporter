using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Editor.UnrealImporter;

/// <summary>
/// Generates sbox .vmat and .vmdl (kv3 text) from processed import data.
/// Structure mirrors Assets/prefabs/capture_point/sm_flagpole_tall_01a.vmdl + .vmat.
/// </summary>
public static class Kv3Writer
{
	static string F( float v ) => v.ToString( "0.0######", CultureInfo.InvariantCulture );

	/// <summary>
	/// Format an Unreal LINEAR tint color as g_vColorTint's "[r g b a]" string.
	/// g_vColorTint is sRGB-gamma in the shader (it does SrgbGammaToLinear), so we sRGB-encode
	/// Unreal's linear value. Null/missing -> white (no tint).
	/// </summary>
	static string ColorTint( float[] c )
	{
		if ( c is null || c.Length < 3 )
			return "[1.000000 1.000000 1.000000 0.000000]";

		float r = LinearToSrgb( c[0] ), g = LinearToSrgb( c[1] ), b = LinearToSrgb( c[2] );
		return $"[{r.ToString( "0.000000", CultureInfo.InvariantCulture )} " +
			$"{g.ToString( "0.000000", CultureInfo.InvariantCulture )} " +
			$"{b.ToString( "0.000000", CultureInfo.InvariantCulture )} 0.000000]";
	}

	static float LinearToSrgb( float c )
	{
		c = System.Math.Clamp( c, 0f, 1f );
		return c <= 0.0031308f ? c * 12.92f : 1.055f * System.MathF.Pow( c, 1f / 2.4f ) - 0.055f;
	}

	/// <summary>Chroma of an HDR color: components divided by the max (null/black -> white).</summary>
	static float[] Normalized( float[] c )
	{
		if ( c is null || c.Length < 3 )
			return new float[] { 1f, 1f, 1f, 1f };

		float max = System.MathF.Max( c[0], System.MathF.Max( c[1], c[2] ) );
		if ( max <= 0f )
			return new float[] { 1f, 1f, 1f, 1f };

		return new[] { c[0] / max, c[1] / max, c[2] / max, 1f };
	}

	/// <summary>
	/// A complex.shader material. Texture arguments are Content-relative paths (forward slashes),
	/// or null to omit that slot. alphaTest picks F_ALPHA_TEST over F_TRANSLUCENT for the alpha
	/// map (UE Masked materials). selfIllumMask enables F_SELF_ILLUM: a grayscale albedo-alpha
	/// mask (selfIllumFromAlbedoAlpha=true, glow tinted by the albedo) or a dedicated RGB
	/// emissive texture. selfIllumBrightness is a LINEAR multiplier (converted to the shader's
	/// pow2 exponent), selfIllumTint an Unreal LINEAR color.
	/// </summary>
	public static string VmatText( string color, string normal, string roughness, string metallic, string ao, string alpha = null,
		string tintMask = null, float[] tintColor = null, float? tintAmount = null, string tintComment = null,
		bool alphaTest = false, string selfIllumMask = null, float[] selfIllumTint = null, float selfIllumBrightness = 1f,
		bool selfIllumFromAlbedoAlpha = false )
	{
		var sb = new StringBuilder();
		sb.AppendLine( "// THIS FILE IS AUTO-GENERATED (unreal_importer)" );
		if ( !string.IsNullOrEmpty( tintComment ) )
			sb.AppendLine( $"// {tintComment}" );
		sb.AppendLine();
		sb.AppendLine( "Layer0" );
		sb.AppendLine( "{" );
		sb.AppendLine( "\tshader \"shaders/complex.shader\"" );
		sb.AppendLine();
		sb.AppendLine( "\t//---- PBR ----" );

		if ( !string.IsNullOrEmpty( metallic ) )
		{
			sb.AppendLine( "\tF_METALNESS_TEXTURE 1" );
		}
		
		sb.AppendLine( "\tF_SPECULAR 1" );
		
		if ( !string.IsNullOrEmpty( tintMask ) )
		{
			sb.AppendLine( "\tF_TINT_MASK 1" );
		}

		if ( !string.IsNullOrEmpty( alpha ) )
		{
			sb.AppendLine();
			sb.AppendLine( "\t//---- Alpha ----" );
			if ( alphaTest )
			{
				sb.AppendLine( "\tF_ALPHA_TEST 1" );
				sb.AppendLine( "\tg_flAlphaTestReference \"0.500\"" );
			}
			else
			{
				sb.AppendLine( "\tF_TRANSLUCENT 1" );
			}
			sb.AppendLine( $"\tTextureTranslucency \"{alpha}\"" );
		}

		if ( !string.IsNullOrEmpty( selfIllumMask ) )
		{
			float mag = MathF.Max( selfIllumBrightness, 0.001f );
			var tint = Normalized( selfIllumTint );
			sb.AppendLine();
			sb.AppendLine( "\t//---- Self Illum ----" );
			sb.AppendLine( "\tF_SELF_ILLUM 1" );
			sb.AppendLine( $"\tTextureSelfIllumMask \"{selfIllumMask}\"" );
			sb.AppendLine( $"\tg_vSelfIllumTint \"{ColorTint( tint )}\"" );
			sb.AppendLine( $"\tg_flSelfIllumBrightness \"{F( Math.Clamp( MathF.Log2( mag ), -10f, 10f ) )}\"" );
			sb.AppendLine( "\tg_flSelfIllumScale \"1.000\"" );
			// Grayscale alpha masks carry no colour - let the albedo tint the glow.
			sb.AppendLine( $"\tg_flSelfIllumAlbedoFactor \"{(selfIllumFromAlbedoAlpha ? "1.000" : "0.000")}\"" );
		}

		if ( !string.IsNullOrEmpty( ao ) )
		{
			sb.AppendLine();
			sb.AppendLine( "\t//---- Ambient Occlusion ----" );
			sb.AppendLine( "\tg_flAmbientOcclusionDirectDiffuse \"0.000\"" );
			sb.AppendLine( "\tg_flAmbientOcclusionDirectSpecular \"0.000\"" );
			sb.AppendLine( $"\tTextureAmbientOcclusion \"{ao}\"" );
		}

		sb.AppendLine();
		sb.AppendLine( "\t//---- Color ----" );
		sb.AppendLine( $"\tg_flModelTintAmount \"{F( tintAmount ?? 1.0f )}\"" );
		sb.AppendLine( $"\tg_vColorTint \"{ColorTint( tintColor )}\"" );
		if ( !string.IsNullOrEmpty( color ) )
			sb.AppendLine( $"\tTextureColor \"{color}\"" );

		if ( !string.IsNullOrEmpty( tintMask ) )
		{
			sb.AppendLine();
			sb.AppendLine( "\t//---- Tint Mask ----" );
			sb.AppendLine( $"\tTextureTintMask \"{tintMask}\"" );
		}

		sb.AppendLine();
		sb.AppendLine( "\t//---- Fog ----" );
		sb.AppendLine( "\tg_bFogEnabled \"1\"" );

		if ( !string.IsNullOrEmpty( metallic ) )
		{
			sb.AppendLine();
			sb.AppendLine( "\t//---- Metalness ----" );
			sb.AppendLine( $"\tTextureMetalness \"{metallic}\"" );
		}

		if ( !string.IsNullOrEmpty( normal ) )
		{
			sb.AppendLine();
			sb.AppendLine( "\t//---- Normal ----" );
			sb.AppendLine( $"\tTextureNormal \"{normal}\"" );
		}

		if ( !string.IsNullOrEmpty( roughness ) )
		{
			sb.AppendLine();
			sb.AppendLine( "\t//---- Roughness ----" );
			sb.AppendLine( "\tg_flRoughnessScaleFactor \"1.000\"" );
			sb.AppendLine( $"\tTextureRoughness \"{roughness}\"" );
		}

		sb.AppendLine();
		sb.AppendLine( "\t//---- Texture Coordinates ----" );
		sb.AppendLine( "\tg_vTexCoordOffset \"[0.000 0.000]\"" );
		sb.AppendLine( "\tg_vTexCoordScale \"[1.000 1.000]\"" );
		sb.AppendLine( "\tg_vTexCoordScrollSpeed \"[0.000 0.000]\"" );
		sb.AppendLine( "}" );

		return sb.ToString();
	}

	/// <summary>
	/// A static model referencing an FBX, with per-slot material remaps, a hull-from-render
	/// collision shape, and a 5-level auto-LOD chain (matches the flagpole reference).
	/// hullMode: "HullPerElement" (default), "SingleHull", "HullPerMesh", or null for no
	/// collision at all - dense foliage geometry can fail hull generation entirely.
	/// mirror emits a ModelModifier_ScaleAndMirror flipping local X - unlike a negative
	/// import_scale (which mirrors but leaves the triangle winding inverted, so faces
	/// get culled from the wrong side), the modifier corrects winding properly.
	/// </summary>
	public static string VmdlText( string fbxContentPath, float importScale, IReadOnlyList<(string slot, string vmat)> remaps, string hullMode = "HullPerElement", bool mirror = false )
	{
		var sb = new StringBuilder();
		sb.AppendLine( "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc30:version{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1} -->" );
		sb.AppendLine( "{" );
		sb.AppendLine( "\trootNode =" );
		sb.AppendLine( "\t{" );
		sb.AppendLine( "\t\t_class = \"RootNode\"" );
		sb.AppendLine( "\t\tchildren =" );
		sb.AppendLine( "\t\t[" );

		// --- Material groups (remaps) ---
		sb.AppendLine( "\t\t\t{" );
		sb.AppendLine( "\t\t\t\t_class = \"MaterialGroupList\"" );
		sb.AppendLine( "\t\t\t\tchildren =" );
		sb.AppendLine( "\t\t\t\t[" );
		sb.AppendLine( "\t\t\t\t\t{" );
		sb.AppendLine( "\t\t\t\t\t\t_class = \"DefaultMaterialGroup\"" );
		sb.AppendLine( "\t\t\t\t\t\tremaps =" );
		sb.AppendLine( "\t\t\t\t\t\t[" );
		foreach ( var (slot, vmat) in remaps )
		{
			sb.AppendLine( "\t\t\t\t\t\t\t{" );
			sb.AppendLine( $"\t\t\t\t\t\t\t\tfrom = \"{slot}\"" );
			sb.AppendLine( $"\t\t\t\t\t\t\t\tto = \"{vmat}\"" );
			sb.AppendLine( "\t\t\t\t\t\t\t}," );
		}
		sb.AppendLine( "\t\t\t\t\t\t]" );
		sb.AppendLine( "\t\t\t\t\t\tuse_global_default = false" );
		sb.AppendLine( "\t\t\t\t\t\tglobal_default_material = \"\"" );
		sb.AppendLine( "\t\t\t\t\t}," );
		sb.AppendLine( "\t\t\t\t]" );
		sb.AppendLine( "\t\t\t}," );

		// --- Mirror (proper winding-corrected flip across local X) ---
		if ( mirror )
		{
			sb.AppendLine( "\t\t\t{" );
			sb.AppendLine( "\t\t\t\t_class = \"ModelModifierList\"" );
			sb.AppendLine( "\t\t\t\tchildren =" );
			sb.AppendLine( "\t\t\t\t[" );
			sb.AppendLine( "\t\t\t\t\t{" );
			sb.AppendLine( "\t\t\t\t\t\t_class = \"ModelModifier_ScaleAndMirror\"" );
			sb.AppendLine( "\t\t\t\t\t\tscale = 1.0" );
			sb.AppendLine( "\t\t\t\t\t\tmirror_x = true" );
			sb.AppendLine( "\t\t\t\t\t\tmirror_y = false" );
			sb.AppendLine( "\t\t\t\t\t\tmirror_z = false" );
			sb.AppendLine( "\t\t\t\t\t\tflip_bone_forward = false" );
			sb.AppendLine( "\t\t\t\t\t\tswap_left_and_right_bones = false" );
			sb.AppendLine( "\t\t\t\t\t}," );
			sb.AppendLine( "\t\t\t\t]" );
			sb.AppendLine( "\t\t\t}," );
		}

		// --- Collision (hull from render mesh) ---
		if ( hullMode is not null )
		{
			sb.AppendLine( "\t\t\t{" );
			sb.AppendLine( "\t\t\t\t_class = \"PhysicsShapeList\"" );
			sb.AppendLine( "\t\t\t\tchildren =" );
			sb.AppendLine( "\t\t\t\t[" );
			sb.AppendLine( "\t\t\t\t\t{" );
			sb.AppendLine( "\t\t\t\t\t\t_class = \"PhysicsHullFromRender\"" );
			sb.AppendLine( "\t\t\t\t\t\tparent_bone = \"\"" );
			sb.AppendLine( "\t\t\t\t\t\tsurface_prop = \"default\"" );
			sb.AppendLine( "\t\t\t\t\t\tcollision_tags = \"solid\"" );
			sb.AppendLine( "\t\t\t\t\t\tfaceMergeAngle = 20.0" );
			sb.AppendLine( "\t\t\t\t\t\tmaxHullVertices = 32" );
			sb.AppendLine( $"\t\t\t\t\t\thull_mode = \"{hullMode}\"" );
			sb.AppendLine( "\t\t\t\t\t}," );
			sb.AppendLine( "\t\t\t\t]" );
			sb.AppendLine( "\t\t\t}," );
		}

		// --- Render mesh (FBX) ---
		sb.AppendLine( "\t\t\t{" );
		sb.AppendLine( "\t\t\t\t_class = \"RenderMeshList\"" );
		sb.AppendLine( "\t\t\t\tchildren =" );
		sb.AppendLine( "\t\t\t\t[" );
		sb.AppendLine( "\t\t\t\t\t{" );
		sb.AppendLine( "\t\t\t\t\t\t_class = \"RenderMeshFile\"" );
		sb.AppendLine( $"\t\t\t\t\t\tfilename = \"{fbxContentPath}\"" );
		sb.AppendLine( "\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]" );
		sb.AppendLine( "\t\t\t\t\t\timport_rotation = [ 0.0, 0.0, 0.0 ]" );
		sb.AppendLine( $"\t\t\t\t\t\timport_scale = {F( importScale )}" );
		sb.AppendLine( "\t\t\t\t\t\talign_origin_x_type = \"None\"" );
		sb.AppendLine( "\t\t\t\t\t\talign_origin_y_type = \"None\"" );
		sb.AppendLine( "\t\t\t\t\t\talign_origin_z_type = \"None\"" );
		sb.AppendLine( "\t\t\t\t\t\tparent_bone = \"\"" );
		sb.AppendLine( "\t\t\t\t\t\timport_filter =" );
		sb.AppendLine( "\t\t\t\t\t\t{" );
		sb.AppendLine( "\t\t\t\t\t\t\texclude_by_default = false" );
		sb.AppendLine( "\t\t\t\t\t\t\texception_list = [  ]" );
		sb.AppendLine( "\t\t\t\t\t\t}" );
		sb.AppendLine( "\t\t\t\t\t}," );
		sb.AppendLine( "\t\t\t\t]" );
		sb.AppendLine( "\t\t\t}," );

		// --- Auto LODs ---
		AppendLodGroupList( sb );

		sb.AppendLine( "\t\t]" );
		sb.AppendLine( "\t\tmodel_archetype = \"\"" );
		sb.AppendLine( "\t\tprimary_associated_entity = \"\"" );
		sb.AppendLine( "\t\tanim_graph_name = \"\"" );
		sb.AppendLine( "\t\tbase_model_name = \"\"" );
		sb.AppendLine( "\t}" );
		sb.AppendLine( "}" );

		return sb.ToString();
	}

	static void AppendLodGroupList( StringBuilder sb )
	{
		// (switch_threshold, simplify_mode, reduction, lock_border, permissive, protect_uv, meshes-on-lod0)
		// Reductions compound down the chain; keep the cumulative ratio (~0.17) gentle enough
		// that low-poly meshes never simplify to 0 triangles - a LOD with no geometry fails
		// the whole model compile (seen with 12-triangle drywall sheets at cumulative 0.04).
		var lods = new (float thr, int mode, float red, bool lockBorder, bool permissive, bool protectUv, bool hasMesh)[]
		{
			( 0.0f, 0, 0.5f, true, false, true, true ),
			( 25.0f, 1, 0.5f, true, false, true, false ),
			( 40.0f, 1, 0.6f, false, true, true, false ),
			( 60.0f, 1, 0.7f, false, true, false, false ),
			( 80.0f, 1, 0.8f, false, true, false, false ),
		};

		sb.AppendLine( "\t\t\t{" );
		sb.AppendLine( "\t\t\t\t_class = \"LODGroupList\"" );
		sb.AppendLine( "\t\t\t\tchildren =" );
		sb.AppendLine( "\t\t\t\t[" );

		foreach ( var l in lods )
		{
			sb.AppendLine( "\t\t\t\t\t{" );
			sb.AppendLine( "\t\t\t\t\t\t_class = \"LODGroup\"" );
			sb.AppendLine( $"\t\t\t\t\t\tswitch_threshold = {F( l.thr )}" );
			sb.AppendLine( $"\t\t\t\t\t\tauto_simplify_mode = {l.mode}" );
			sb.AppendLine( $"\t\t\t\t\t\tauto_reduction = {F( l.red )}" );
			sb.AppendLine( "\t\t\t\t\t\tauto_max_error = 0.0" );
			sb.AppendLine( $"\t\t\t\t\t\tauto_lock_border_vertices = {B( l.lockBorder )}" );
			sb.AppendLine( $"\t\t\t\t\t\tauto_permissive_simplification = {B( l.permissive )}" );
			sb.AppendLine( $"\t\t\t\t\t\tauto_protect_uv_seams = {B( l.protectUv )}" );
			sb.AppendLine( "\t\t\t\t\t\tauto_regularize = 1" );
			sb.AppendLine( "\t\t\t\t\t\tauto_prune_isolated_components = false" );
			sb.AppendLine( "\t\t\t\t\t\tauto_strip_vertex_color = false" );
			sb.AppendLine( "\t\t\t\t\t\tauto_material_culling_enabled = false" );
			sb.AppendLine( "\t\t\t\t\t\tmeshes =" );
			if ( l.hasMesh )
			{
				sb.AppendLine( "\t\t\t\t\t\t[" );
				sb.AppendLine( "\t\t\t\t\t\t\t\"unnamed_1\"," );
				sb.AppendLine( "\t\t\t\t\t\t]" );
			}
			else
			{
				sb.AppendLine( "\t\t\t\t\t\t[  ]" );
			}
			sb.AppendLine( "\t\t\t\t\t\tmaterial_culls = [  ]" );
			sb.AppendLine( "\t\t\t\t\t}," );
		}

		sb.AppendLine( "\t\t\t\t]" );
		sb.AppendLine( "\t\t\t}," );
	}

	static string B( bool v ) => v ? "true" : "false";
}
