using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Sandbox;

namespace Editor.UnrealImporter;

/// <summary>
/// Extracts the editor thumbnail Unreal embeds in every saved (uncooked) .uasset.
///
/// The package stores an FObjectThumbnail: 12 bytes of header (int32 width, height,
/// compressedSize) followed by the compressed image - PNG normally, JPEG in newer
/// packs (flagged by a negative height). Rather than parsing the version-dependent
/// package summary to find the thumbnail table, we scan for the PNG/JPEG magic and
/// validate the header that precedes it - verified against UE 5.x Fab packs.
///
/// Extracted images are cached on disk under the project's .sbox/ folder (keyed on
/// file mtime+size, so re-saved assets re-extract), plus an in-memory Pixmap cache
/// because the import window rebuilds its list on every keystroke.
/// </summary>
public static class UassetThumbnail
{
	// path -> pixmap (null = scanned, no thumbnail found)
	static readonly Dictionary<string, Pixmap> memoryCache = new();
	static readonly Dictionary<string, Task<Pixmap>> inFlight = new();

	static string CacheDir => Sandbox.Project.Current is not null
		? Path.Combine( Sandbox.Project.Current.GetRootPath(), ".sbox", "unrealimporter", "thumbnails" )
		: Path.Combine( Path.GetTempPath(), "unrealimporter", "thumbnails" );

	/// <summary>Memory-cache lookup. True if this path has been resolved (pixmap may still be null).</summary>
	public static bool TryGetCached( string absPath, out Pixmap pixmap )
		=> memoryCache.TryGetValue( absPath, out pixmap );

	/// <summary>
	/// Resolve the thumbnail for a .uasset: memory cache, then disk cache, then a scan of the
	/// file itself. Returns null if the asset has no embedded thumbnail. Safe to call
	/// repeatedly - concurrent requests for the same path share one task.
	/// </summary>
	public static Task<Pixmap> LoadAsync( string absPath )
	{
		if ( memoryCache.TryGetValue( absPath, out var cached ) )
			return Task.FromResult( cached );

		if ( inFlight.TryGetValue( absPath, out var running ) )
			return running;

		var task = Load( absPath );
		inFlight[absPath] = task;
		return task;
	}

	static async Task<Pixmap> Load( string absPath )
	{
		string imagePath = null;
		try
		{
			// File IO + scanning off the main thread; only the Pixmap itself is created back on it.
			imagePath = await Task.Run( () => ResolveCacheFile( absPath ) );
		}
		catch ( Exception e )
		{
			Log.Warning( $"Thumbnail extraction failed for {absPath}: {e.Message}" );
		}

		var pixmap = imagePath is not null ? Pixmap.FromFile( imagePath ) : null;
		memoryCache[absPath] = pixmap;
		inFlight.Remove( absPath );
		return pixmap;
	}

	/// <summary>
	/// Path to a cached thumbnail image for this uasset, extracting it if needed.
	/// Null if the asset has no embedded thumbnail (recorded with a .none marker).
	/// </summary>
	static string ResolveCacheFile( string absPath )
	{
		var fi = new FileInfo( absPath );
		if ( !fi.Exists )
			return null;

		var pathHash = ShortHash( absPath.ToLowerInvariant() );
		var statHash = ShortHash( $"{fi.LastWriteTimeUtc.Ticks}|{fi.Length}" );
		var dir = CacheDir;
		var baseName = Path.Combine( dir, $"{pathHash}_{statHash}" );

		if ( File.Exists( baseName + ".png" ) ) return baseName + ".png";
		if ( File.Exists( baseName + ".jpg" ) ) return baseName + ".jpg";
		if ( File.Exists( baseName + ".none" ) ) return null;

		Directory.CreateDirectory( dir );

		// The asset changed since it was last cached - drop the stale entries for this path.
		foreach ( var stale in Directory.EnumerateFiles( dir, pathHash + "_*" ) )
			File.Delete( stale );

		var (image, ext) = Extract( File.ReadAllBytes( absPath ) );
		if ( image is null )
		{
			File.WriteAllBytes( baseName + ".none", Array.Empty<byte>() );
			return null;
		}

		var target = baseName + ext;
		File.WriteAllBytes( target, image );
		return target;
	}

	static string ShortHash( string input )
		=> Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( input ) ) )[..16].ToLowerInvariant();

	static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

	/// <summary>Find the embedded thumbnail in raw .uasset bytes, or (null, null).</summary>
	internal static (byte[] Image, string Extension) Extract( byte[] data )
	{
		if ( TryFindImage( data, PngMagic, out var png ) )
			return (png, ".png");

		// JPEG: SOI (FF D8 FF) followed by an APP0/APP1/DQT segment.
		if ( TryFindJpeg( data, out var jpg ) )
			return (jpg, ".jpg");

		return (null, null);
	}

	static bool TryFindImage( byte[] data, byte[] magic, out byte[] image )
	{
		int pos = 12;
		while ( (pos = IndexOf( data, magic, pos )) >= 0 )
		{
			if ( TrySlice( data, pos, out image ) )
				return true;
			pos += 1;
		}

		image = null;
		return false;
	}

	static bool TryFindJpeg( byte[] data, out byte[] image )
	{
		for ( int pos = 12; pos < data.Length - 4; pos++ )
		{
			if ( data[pos] != 0xFF || data[pos + 1] != 0xD8 || data[pos + 2] != 0xFF )
				continue;
			var seg = data[pos + 3];
			if ( seg != 0xE0 && seg != 0xE1 && seg != 0xDB )
				continue;
			if ( TrySlice( data, pos, out image ) )
				return true;
		}

		image = null;
		return false;
	}

	/// <summary>
	/// Validate the FObjectThumbnail header in the 12 bytes before the image magic and
	/// slice out the image. Rejects magic hits that aren't preceded by a sane header.
	/// </summary>
	static bool TrySlice( byte[] data, int magicPos, out byte[] image )
	{
		image = null;
		if ( magicPos < 12 )
			return false;

		int width = BitConverter.ToInt32( data, magicPos - 12 );
		int height = Math.Abs( BitConverter.ToInt32( data, magicPos - 8 ) );   // negative = JPEG flag
		int size = BitConverter.ToInt32( data, magicPos - 4 );

		if ( width < 4 || width > 8192 || height < 4 || height > 8192 )
			return false;
		if ( size < 16 || (long)magicPos + size > data.Length )
			return false;

		image = data[magicPos..(magicPos + size)];
		return true;
	}

	static int IndexOf( byte[] haystack, byte[] needle, int start )
	{
		var idx = haystack.AsSpan( start ).IndexOf( needle );
		return idx < 0 ? -1 : start + idx;
	}
}
