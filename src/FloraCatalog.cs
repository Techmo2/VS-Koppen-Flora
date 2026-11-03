using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using Vintagestory.ServerMods.NoObf;

namespace KoppenFlora;

/// <summary>
/// Every <see cref="FloraProfile"/> the loaded mods supply, and which tree generators, block patches
/// and fruit tree types each one governs. Anything without a profile keeps vanilla's rules.
///
/// One target can have several profiles: a vanilla tree standing in for relatives with different
/// climates (pedunculate oak, and the southern oaks) gets one per envelope, and the best fit at a
/// place decides (<see cref="ProfileSet"/>).
/// </summary>
public sealed class FloraCatalog
{
    public const string AssetFolder = "config/flora/";

    private readonly Dictionary<string, ProfileSet> _byTreeGen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProfileSet> _byFruitTree = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Wildcard, FloraProfile Profile)> _patchRules = new();
    private readonly ConditionalWeakTable<BlockPatch, PatchMatch> _patchCache = new();

    private sealed class PatchMatch
    {
        public ProfileSet Profiles;
    }

    public IReadOnlyCollection<FloraProfile> Profiles { get; }

    private FloraCatalog(IReadOnlyCollection<FloraProfile> profiles)
    {
        Profiles = profiles;
        foreach (FloraProfile profile in profiles)
        {
            foreach (string gen in profile.TreeGens ?? Array.Empty<string>()) Add(_byTreeGen, gen, profile);
            foreach (string type in profile.FruitTrees ?? Array.Empty<string>()) Add(_byFruitTree, type, profile);
            foreach (string code in profile.Patches ?? Array.Empty<string>()) _patchRules.Add((code, profile));
        }
    }

    /// <summary>Reads <see cref="AssetFolder"/> from every domain. A file that cannot be read stops the server with its name.</summary>
    public static FloraCatalog Load(ICoreAPI api)
    {
        var byId = new Dictionary<string, FloraProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (IAsset asset in api.Assets.GetMany(AssetFolder).OrderBy(a => a.Location.ToString(), StringComparer.Ordinal))
        {
            if (!asset.Location.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            FloraProfile[] profiles;
            try
            {
                profiles = JsonConvert.DeserializeObject<FloraProfile[]>(asset.ToText());
                foreach (FloraProfile profile in profiles) profile.Prepare();
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"[koppenflora] {asset.Location} could not be read: {e.Message}", e);
            }
            foreach (FloraProfile profile in profiles) byId[profile.Id] = profile;
        }
        return new FloraCatalog(byId.Values.ToArray());
    }

    private static void Add(Dictionary<string, ProfileSet> map, string key, FloraProfile profile)
    {
        if (!map.TryGetValue(key, out ProfileSet set)) map[key] = set = new ProfileSet();
        set.Add(profile);
    }

    public ProfileSet ForTreeGen(AssetLocation generator) =>
        generator != null && _byTreeGen.TryGetValue(generator.Path, out ProfileSet set) ? set : null;

    public ProfileSet ForFruitTree(string type) =>
        type != null && _byFruitTree.TryGetValue(type, out ProfileSet set) ? set : null;

    /// <summary>The profiles governing a block patch: every rule matching any of its block codes.</summary>
    public ProfileSet ForPatch(BlockPatch patch)
    {
        if (_patchCache.TryGetValue(patch, out PatchMatch match)) return match.Profiles;

        ProfileSet found = null;
        foreach ((string wildcard, FloraProfile profile) in _patchRules)
        {
            if (patch.blockCodes?.Any(code => code != null && WildcardUtil.Match(wildcard, code.Path)) == true)
                (found ??= new ProfileSet()).Add(profile);
        }
        _patchCache.AddOrUpdate(patch, new PatchMatch { Profiles = found });
        return found;
    }

    /// <summary>Tree generators, fruit types and patch codes no profile covers, for the startup log.</summary>
    public IEnumerable<string> Unmatched(IEnumerable<string> treeGens, IEnumerable<BlockPatch> patches, IEnumerable<string> fruitTypes)
    {
        foreach (string gen in treeGens.Distinct()) if (!_byTreeGen.ContainsKey(gen)) yield return "tree " + gen;
        foreach (string type in fruitTypes.Distinct()) if (!_byFruitTree.ContainsKey(type)) yield return "fruit tree " + type;
        foreach (BlockPatch patch in patches)
            if (ForPatch(patch) == null) yield return "patch " + patch.blockCodes?.FirstOrDefault()?.Path;
    }
}

/// <summary>
/// The profiles governing one target. They must agree on <see cref="FloraProfile.Mode"/>; at a place,
/// the most suitable one decides, with its weight.
/// </summary>
public sealed class ProfileSet
{
    private readonly List<FloraProfile> _profiles = new();

    public void Add(FloraProfile profile)
    {
        if (_profiles.Count > 0 && _profiles[0].Multiplies != profile.Multiplies)
            throw new InvalidOperationException($"[koppenflora] '{profile.Id}' and '{_profiles[0].Id}' govern the same target with different modes");
        _profiles.Add(profile);
    }

    public bool Multiplies => _profiles[0].Multiplies;

    /// <summary>A stable name for random rolls: the first profile's id.</summary>
    public string Id => _profiles[0].Id;

    /// <summary>The best suitability among the profiles, and that profile's weight.</summary>
    public float Suitability(in ClimateSnapshot climate, out float weight)
    {
        float best = 0f;
        weight = _profiles[0].Weight;
        foreach (FloraProfile profile in _profiles)
        {
            float s = profile.Suitability(climate);
            if (s > best)
            {
                best = s;
                weight = profile.Weight;
            }
        }
        return best;
    }

    public float Suitability(in ClimateSnapshot climate) => Suitability(climate, out _);
}
