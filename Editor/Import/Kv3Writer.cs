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

	/// <summary>
	/// A complex.shader material. Texture arguments are Content-relative paths (forward slashes),
	/// or null to omit that slot.
	/// </summary>
	public static string VmatText( string color, string normal, string roughness, string metallic, string ao, string alpha = null,
		string tintMask = null, float[] tintColor = null, float? tintAmount = null )
	{
		var sb = new StringBuilder();
		sb.AppendLine( "// THIS FILE IS AUTO-GENERATED (unreal_importer)" );
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
			sb.AppendLine( "\tF_TRANSLUCENT 1" );
			sb.AppendLine( $"\tTextureTranslucency \"{alpha}\"" );
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
	/// </summary>
	public static string VmdlText( string fbxContentPath, float importScale, IReadOnlyList<(string slot, string vmat)> remaps )
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

		// --- Collision (hull from render mesh) ---
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
		sb.AppendLine( "\t\t\t\t\t\thull_mode = \"HullPerElement\"" );
		sb.AppendLine( "\t\t\t\t\t}," );
		sb.AppendLine( "\t\t\t\t]" );
		sb.AppendLine( "\t\t\t}," );

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
		var lods = new (float thr, int mode, float red, bool lockBorder, bool permissive, bool protectUv, bool hasMesh)[]
		{
			( 0.0f, 0, 0.5f, true, false, true, true ),
			( 25.0f, 1, 0.5f, true, false, true, false ),
			( 40.0f, 1, 0.5f, false, true, true, false ),
			( 60.0f, 1, 0.45f, false, true, false, false ),
			( 80.0f, 1, 0.4f, false, true, false, false ),
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
