using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Editor.UnrealImporter;

public class MeshStats
{
	public long Triangles { get; set; } = -1;
	public long Vertices { get; set; } = -1;
	public long Materials { get; set; } = -1;
	public long LODs { get; set; } = -1;

	/// <summary>Source file stamp this was read from - stale entries re-parse.</summary>
	public long Mtime { get; set; }
	public long Size { get; set; }
}

/// <summary>
/// Reads triangle/vertex counts straight out of an uncooked StaticMesh .uasset, no Unreal
/// involved. The Unreal editor bakes asset-registry tags ("Triangles", "Vertices",
/// "Materials", "LODs", ...) into every saved package as serialized FString key/value
/// pairs: int32 length (incl. NUL), ascii chars, NUL. Rather than parsing the
/// version-dependent FPackageFileSummary to find the block, we scan for that
/// self-contained byte pattern - same magic-scan approach the thumbnail extractor uses,
/// verified against UE 5.x Fab packs.
///
/// Cached in memory and on disk (.sbox/unrealimporter/meshstats.json, keyed mtime+size)
/// because a full pack means gigabytes of .uasset reads otherwise.
/// </summary>
public static class UassetMeshStats
{
	// Registry tags live in the package header tables, which sit well before the bulk
	// mesh data - reading the head of the file is nearly always enough.
	const int HeaderReadBytes = 4 * 1024 * 1024;

	static readonly ConcurrentDictionary<string, MeshStats> cache = new( StringComparer.OrdinalIgnoreCase );
	static readonly ConcurrentDictionary<string, Task<MeshStats>> inFlight = new( StringComparer.OrdinalIgnoreCase );
	static readonly SemaphoreSlim ioGate = new( 2 );   // don't hammer the disk when a folder expands

	static bool diskCacheLoaded;
	static int saveScheduled;

	static string CacheFile => Sandbox.Project.Current is not null
		? Path.Combine( Sandbox.Project.Current.GetRootPath(), ".sbox", "unrealimporter", "meshstats.json" )
		: Path.Combine( Path.GetTempPath(), "unrealimporter", "meshstats.json" );

	/// <summary>Memory/disk cache lookup, no file IO on the asset itself.</summary>
	public static bool TryGetCached( string absPath, out MeshStats stats )
	{
		LoadDiskCache();

		if ( cache.TryGetValue( absPath, out stats ) )
		{
			var fi = new FileInfo( absPath );
			if ( fi.Exists && fi.LastWriteTimeUtc.Ticks == stats.Mtime && fi.Length == stats.Size )
				return true;

			cache.TryRemove( absPath, out _ );
			stats = null;
		}

		return false;
	}

	/// <summary>Parse (or fetch cached) stats for one .uasset. Null when nothing was found.</summary>
	public static Task<MeshStats> LoadAsync( string absPath )
	{
		if ( TryGetCached( absPath, out var cached ) )
			return Task.FromResult( cached );

		return inFlight.GetOrAdd( absPath, p => Task.Run( async () =>
		{
			try
			{
				await ioGate.WaitAsync();
				try
				{
					var stats = Parse( p );
					if ( stats is not null )
					{
						cache[p] = stats;
						ScheduleSave();
					}
					return stats;
				}
				finally
				{
					ioGate.Release();
				}
			}
			catch
			{
				return null;
			}
			finally
			{
				inFlight.TryRemove( p, out _ );
			}
		} ) );
	}

	static MeshStats Parse( string absPath )
	{
		var fi = new FileInfo( absPath );
		if ( !fi.Exists )
			return null;

		var data = ReadHead( absPath, HeaderReadBytes );
		var tris = TagValue( data, "Triangles" );

		// Rare: huge header tables push the tag block past our head read.
		if ( tris < 0 && fi.Length > data.Length )
		{
			data = File.ReadAllBytes( absPath );
			tris = TagValue( data, "Triangles" );
		}

		if ( tris < 0 )
			return null;

		return new MeshStats
		{
			Triangles = tris,
			Vertices = TagValue( data, "Vertices" ),
			Materials = TagValue( data, "Materials" ),
			LODs = TagValue( data, "LODs" ),
			Mtime = fi.LastWriteTimeUtc.Ticks,
			Size = fi.Length,
		};
	}

	static byte[] ReadHead( string path, int maxBytes )
	{
		using var fs = File.OpenRead( path );
		var len = (int)Math.Min( fs.Length, maxBytes );
		var buf = new byte[len];
		fs.ReadExactly( buf, 0, len );
		return buf;
	}

	/// <summary>
	/// Find asset-registry tag <paramref name="key"/> and return its numeric value, -1 when
	/// absent. Matches the FString serialization (length prefix + NUL) so plain-text
	/// occurrences of the word elsewhere can't false-positive.
	/// </summary>
	static long TagValue( ReadOnlySpan<byte> data, string key )
	{
		Span<byte> pattern = stackalloc byte[4 + key.Length + 1];
		BitConverter.TryWriteBytes( pattern, key.Length + 1 );
		Encoding.ASCII.GetBytes( key, pattern[4..] );
		pattern[^1] = 0;

		var at = data.IndexOf( pattern );
		if ( at < 0 )
			return -1;

		var vpos = at + pattern.Length;
		if ( vpos + 4 > data.Length )
			return -1;

		int vlen = BitConverter.ToInt32( data[vpos..] );
		if ( vlen <= 1 || vlen > 64 || vpos + 4 + vlen > data.Length )
			return -1;

		var s = Encoding.ASCII.GetString( data.Slice( vpos + 4, vlen - 1 ) );
		return long.TryParse( s, out var v ) ? v : -1;
	}

	// ---- disk cache ----

	static void LoadDiskCache()
	{
		if ( diskCacheLoaded )
			return;
		diskCacheLoaded = true;

		try
		{
			if ( !File.Exists( CacheFile ) )
				return;

			var loaded = JsonSerializer.Deserialize<ConcurrentDictionary<string, MeshStats>>( File.ReadAllText( CacheFile ) );
			if ( loaded is null )
				return;

			foreach ( var kv in loaded )
				cache.TryAdd( kv.Key, kv.Value );
		}
		catch
		{
			// cache is disposable - a corrupt file just means re-parsing
		}
	}

	static void ScheduleSave()
	{
		if ( Interlocked.Exchange( ref saveScheduled, 1 ) == 1 )
			return;

		_ = Task.Run( async () =>
		{
			await Task.Delay( 3000 );
			Interlocked.Exchange( ref saveScheduled, 0 );

			try
			{
				Directory.CreateDirectory( Path.GetDirectoryName( CacheFile ) );
				File.WriteAllText( CacheFile, JsonSerializer.Serialize( cache ) );
			}
			catch
			{
			}
		} );
	}
}
