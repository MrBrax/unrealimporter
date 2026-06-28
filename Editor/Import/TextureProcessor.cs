using System;
using System.Collections.Generic;
using System.IO;
using Sandbox;

namespace Editor.UnrealImporter;

/// <summary>
/// Output texture filenames (no path) for a processed material, or null where absent.
/// </summary>
public class ProcessedTextures
{
	public string Color;
	public string Alpha;
	public string Normal;
	public string Roughness;
	public string Metallic;
	public string Ao;
	public string Emissive;
	public string TintMask;

	/// <summary>True when multi-zone tints were baked into Color (so the vmat must NOT re-tint).</summary>
	public bool TintBaked;
}

/// <summary>
/// Turns Unreal's raw exported textures into sbox-ready ones using sbox's Bitmap:
///  - splits RMA (R=roughness, G=metallic, B=ao) into separate grayscale maps
///  - flips the normal's green channel (Unreal DirectX -> sbox OpenGL)
///  - extracts the albedo's alpha to a separate map
///  - writes everything as &lt;base&gt;_&lt;role&gt;.png (lowercase, no dots)
/// </summary>
public static class TextureProcessor
{
	public static ProcessedTextures Process( ManifestMaterial mat, string stagingDir, string outputTextureDir, string baseName )
	{
		Directory.CreateDirectory( outputTextureDir );
		var result = new ProcessedTextures();

		// --- Color (+ alpha) ---
		if ( !string.IsNullOrEmpty( mat.Alb ) )
		{
			using var alb = Load( stagingDir, mat.Alb );
			if ( alb is not null )
			{
				// Multi-zone tint masks (per-channel tint colors) can't be expressed by the
				// single-tint complex shader, so bake them straight into the albedo - this is
				// what Unreal renders. Single uniform tints stay dynamic (handled in the vmat).
				if ( mat.TintZones is { Count: > 0 } )
				{
					using var mask = string.IsNullOrEmpty( mat.TintMask ) ? null : Load( stagingDir, mat.TintMask );
					BakeTintZones( alb, mask, mat.TintZones, mat.ScalarParams );
					result.TintBaked = true;
				}

				result.Color = Save( alb, outputTextureDir, baseName, "color" );

				if ( !alb.IsOpaque() )
					result.Alpha = Save( ExtractAlpha( alb ), outputTextureDir, baseName, "alpha", dispose: true );
			}
		}

		// --- Normal (flip green) ---
		if ( !string.IsNullOrEmpty( mat.Nrm ) )
		{
			using var nrm = Load( stagingDir, mat.Nrm );
			if ( nrm is not null )
				result.Normal = Save( FlipGreen( nrm ), outputTextureDir, baseName, "normal", dispose: true );
		}

		// --- RMA pack -> roughness / metallic / ao ---
		if ( !string.IsNullOrEmpty( mat.Rma ) )
		{
			using var rma = Load( stagingDir, mat.Rma );
			if ( rma is not null )
			{
				result.Roughness = Save( ExtractChannel( rma, 0 ), outputTextureDir, baseName, "roughness", dispose: true );
				result.Metallic = Save( ExtractChannel( rma, 1 ), outputTextureDir, baseName, "metallic", dispose: true );
				result.Ao = Save( ExtractChannel( rma, 2 ), outputTextureDir, baseName, "ao", dispose: true );
			}
		}

		// --- Explicit single-channel maps (override RMA-derived if both somehow present) ---
		ProcessSingle( mat.Rough, stagingDir, outputTextureDir, baseName, "roughness", ref result.Roughness );
		ProcessSingle( mat.Metal, stagingDir, outputTextureDir, baseName, "metallic", ref result.Metallic );
		ProcessSingle( mat.Ao, stagingDir, outputTextureDir, baseName, "ao", ref result.Ao );
		ProcessSingle( mat.Emissive, stagingDir, outputTextureDir, baseName, "emissive", ref result.Emissive );

		// --- Tint mask (grayscale; complex shader packs it into the normal's alpha) ---
		// Only when not already baked into the albedo above.
		if ( !result.TintBaked && !string.IsNullOrEmpty( mat.TintMask ) )
		{
			using var mask = Load( stagingDir, mat.TintMask );
			if ( mask is not null )
				result.TintMask = Save( ExtractChannel( mask, 0 ), outputTextureDir, baseName, "tintmask", dispose: true );
		}

		return result;
	}

	static void ProcessSingle( string rel, string stagingDir, string outDir, string baseName, string role, ref string slot )
	{
		if ( string.IsNullOrEmpty( rel ) )
			return;

		using var bmp = Load( stagingDir, rel );
		if ( bmp is not null )
			slot = Save( bmp, outDir, baseName, role );
	}

	static Bitmap Load( string stagingDir, string relPath )
	{
		var abs = Path.Combine( stagingDir, relPath.Replace( '/', Path.DirectorySeparatorChar ) );
		if ( !File.Exists( abs ) )
			return null;

		var bmp = Bitmap.CreateFromBytes( File.ReadAllBytes( abs ) );
		return bmp is not null && bmp.IsValid ? bmp : null;
	}

	static string Save( Bitmap bmp, string outDir, string baseName, string role, bool dispose = false )
	{
		var fileName = $"{baseName}_{role}.png";
		File.WriteAllBytes( Path.Combine( outDir, fileName ), bmp.ToPng() );
		if ( dispose )
			bmp.Dispose();

		return fileName;
	}

	/// <summary>
	/// Bake per-channel tint colors into the albedo (in place). For each zone, the albedo is
	/// multiplied toward (albedo * tint) by that mask channel: lerp(alb, alb*tint, mask.channel).
	/// Mirrors Unreal's multi-tint workflow so the imported model matches its source look.
	/// Tint colors are LINEAR (Unreal LinearColor); the multiply is done in linear space.
	/// </summary>
	static void BakeTintZones( Bitmap alb, Bitmap mask, Dictionary<string, float[]> zones, Dictionary<string, float> scalars )
	{
		var px = alb.GetPixels();
		int w = alb.Width, h = alb.Height;

		Color[] mpx = null;
		int mw = 0, mh = 0;
		if ( mask is not null )
		{
			mpx = mask.GetPixels();
			mw = mask.Width;
			mh = mask.Height;
		}

		// Unreal's "Tint Mask Multi" scales the mask strength; default 1.
		float multi = 1f;
		if ( scalars is not null && scalars.TryGetValue( "Tint Mask Multi", out var mv ) && mv > 0f )
			multi = mv;

		var zoneList = new List<(int ch, float r, float g, float b)>();
		foreach ( var (key, c) in zones )
		{
			int ch = key switch { "r" => 0, "g" => 1, "b" => 2, "a" => 3, _ => -1 };
			if ( ch < 0 || c is null || c.Length < 3 )
				continue;

			zoneList.Add( (ch, c[0], c[1], c[2]) );
		}

		for ( int y = 0; y < h; y++ )
		{
			for ( int x = 0; x < w; x++ )
			{
				int i = y * w + x;
				var c = px[i];
				float lr = SrgbToLinear( c.r ), lg = SrgbToLinear( c.g ), lb = SrgbToLinear( c.b );

				Color m = default;
				if ( mpx is not null )
				{
					int mx = mw == w ? x : x * mw / w;
					int my = mh == h ? y : y * mh / h;
					m = mpx[my * mw + mx];
				}

				foreach ( var z in zoneList )
				{
					float maskV = mpx is null ? 1f : (z.ch == 0 ? m.r : z.ch == 1 ? m.g : z.ch == 2 ? m.b : m.a);
					maskV = Math.Clamp( maskV * multi, 0f, 1f );
					lr = Lerp( lr, lr * z.r, maskV );
					lg = Lerp( lg, lg * z.g, maskV );
					lb = Lerp( lb, lb * z.b, maskV );
				}

				px[i] = new Color( LinearToSrgb( lr ), LinearToSrgb( lg ), LinearToSrgb( lb ), c.a );
			}
		}

		alb.SetPixels( px );
	}

	static float Lerp( float a, float b, float t ) => a + (b - a) * t;

	static float SrgbToLinear( float c )
		=> c <= 0.04045f ? c / 12.92f : MathF.Pow( (c + 0.055f) / 1.055f, 2.4f );

	static float LinearToSrgb( float c )
	{
		c = Math.Clamp( c, 0f, 1f );
		return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow( c, 1f / 2.4f ) - 0.055f;
	}

	/// <summary>New grayscale bitmap from one channel (0=R, 1=G, 2=B).</summary>
	static Bitmap ExtractChannel( Bitmap src, int channel )
	{
		var pixels = src.GetPixels();
		for ( int i = 0; i < pixels.Length; i++ )
		{
			var c = pixels[i];
			float v = channel == 0 ? c.r : channel == 1 ? c.g : c.b;
			pixels[i] = new Color( v, v, v, 1f );
		}

		var bmp = new Bitmap( src.Width, src.Height );
		bmp.SetPixels( pixels );
		return bmp;
	}

	/// <summary>New bitmap with the green channel inverted (DirectX -> OpenGL normals).</summary>
	static Bitmap FlipGreen( Bitmap src )
	{
		var pixels = src.GetPixels();
		for ( int i = 0; i < pixels.Length; i++ )
		{
			var c = pixels[i];
			pixels[i] = new Color( c.r, 1f - c.g, c.b, c.a );
		}

		var bmp = new Bitmap( src.Width, src.Height );
		bmp.SetPixels( pixels );
		return bmp;
	}

	/// <summary>New grayscale bitmap holding the source alpha.</summary>
	static Bitmap ExtractAlpha( Bitmap src )
	{
		var pixels = src.GetPixels();
		for ( int i = 0; i < pixels.Length; i++ )
		{
			float a = pixels[i].a;
			pixels[i] = new Color( a, a, a, 1f );
		}

		var bmp = new Bitmap( src.Width, src.Height );
		bmp.SetPixels( pixels );
		return bmp;
	}
}
