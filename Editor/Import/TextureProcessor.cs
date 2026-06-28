using System;
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
				// Export the albedo UNTOUCHED. Fab/Megascans albedos already contain the final
				// colours; the material's tint mask + tint colours are an OPTIONAL runtime-recolour
				// system (team colours / variants). Baking them here double-colours and corrupts
				// the result, so we keep the albedo pristine and leave tint inert in the vmat.
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

		// --- Tint mask (grayscale) - export the populated channel so it can drive optional
		// runtime tinting. Masks are single-channel but the data isn't always in R (this ATV
		// mask lives in B), so pick whichever channel actually carries data.
		if ( !string.IsNullOrEmpty( mat.TintMask ) )
		{
			using var mask = Load( stagingDir, mat.TintMask );
			if ( mask is not null )
				result.TintMask = Save( ExtractChannel( mask, DominantChannel( mask ) ), outputTextureDir, baseName, "tintmask", dispose: true );
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

	/// <summary>Index (0=R,1=G,2=B) of the channel carrying the mask data (widest value range).</summary>
	static int DominantChannel( Bitmap src )
	{
		var px = src.GetPixels();
		float minR = 1, maxR = 0, minG = 1, maxG = 0, minB = 1, maxB = 0;

		// Sample sparsely - masks are large and uniform enough that this is plenty.
		int step = Math.Max( 1, px.Length / 100000 );
		for ( int i = 0; i < px.Length; i += step )
		{
			var c = px[i];
			if ( c.r < minR ) minR = c.r; if ( c.r > maxR ) maxR = c.r;
			if ( c.g < minG ) minG = c.g; if ( c.g > maxG ) maxG = c.g;
			if ( c.b < minB ) minB = c.b; if ( c.b > maxB ) maxB = c.b;
		}

		float rangeR = maxR - minR, rangeG = maxG - minG, rangeB = maxB - minB;
		if ( rangeB >= rangeR && rangeB >= rangeG ) return 2;
		if ( rangeG >= rangeR && rangeG >= rangeB ) return 1;
		return 0;
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
