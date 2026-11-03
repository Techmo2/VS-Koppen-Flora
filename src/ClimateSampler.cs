using System;
using System.Collections.Generic;
using VSTerrainDiffusion.Api;

namespace KoppenFlora;

/// <summary>
/// Climate snapshots for the columns world generation asks about, cached per thread for the chunk
/// being generated: the vegetation pass asks about the same columns again and again (every patch
/// and tree try), and a snapshot rebuilds twelve months and thirty Köppen weights.
/// </summary>
internal static class ClimateSampler
{
    [ThreadStatic] private static Dictionary<long, ClimateSnapshot> _cache;
    [ThreadStatic] private static WeakReference<TerrainClimate> _cacheOwner;

    /// <summary>Cleared past this many columns, which a chunk's own columns plus a ring around it stay under.</summary>
    private const int MaxCached = 4096;

    public static bool TryGet(TerrainClimate climate, int x, int z, Koppen.Softness softness, out ClimateSnapshot snapshot)
    {
        _cache ??= new Dictionary<long, ClimateSnapshot>();
        if (_cacheOwner == null || !_cacheOwner.TryGetTarget(out TerrainClimate owner) || owner != climate)
        {
            // A new world: its climate is not the last one's.
            _cache.Clear();
            _cacheOwner = new WeakReference<TerrainClimate>(climate);
        }

        long key = ((long)x << 32) | (uint)z;
        if (_cache.TryGetValue(key, out snapshot)) return true;

        if (!climate.TryGetClimate(x, z, out ClimateRecord record))
        {
            snapshot = default;
            return false;
        }

        snapshot = new ClimateSnapshot(record, softness);
        if (_cache.Count >= MaxCached) _cache.Clear();
        _cache[key] = snapshot;
        return true;
    }
}
