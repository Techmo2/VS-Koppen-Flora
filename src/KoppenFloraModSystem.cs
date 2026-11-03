using System.Linq;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.ServerMods.NoObf;
using VSTerrainDiffusion;
using VSTerrainDiffusion.Api;

namespace KoppenFlora;

/// <summary>
/// Loads the flora profiles and hooks vanilla's vegetation pass. On a world Terrain Diffusion is not
/// generating, its climate API is null and every hook leaves the decision to vanilla.
/// </summary>
public class KoppenFloraModSystem : ModSystem
{
    private Harmony _harmony;
    private ICoreServerAPI _api;

    public FloraCatalog Catalog { get; private set; }

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        _api = api;
        Catalog = FloraCatalog.Load(api);

        var terrain = api.ModLoader.GetModSystem<TerrainDiffusionModSystem>();
        VegetationHooks.Current = new VegetationHooks.Context
        {
            Catalog = Catalog,
            Climate = () => terrain?.Climate,
            Softness = Koppen.Softness.Default,
            Seed = api.World.Seed
        };

        _harmony = new Harmony(VegetationHooks.HarmonyId);
        var missing = VegetationHooks.Apply(_harmony);
        if (missing.Count > 0)
            api.Logger.Warning("[koppenflora] This game version lacks {0}; those parts keep vanilla's rules.", string.Join(", ", missing));

        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, LogCoverage);
    }

    /// <summary>What has a profile and what keeps vanilla's rules, once the worldgen configs are loaded.</summary>
    private void LogCoverage()
    {
        if (_api == null) return;
        var trees = _api.Assets.Get("worldgen/treengenproperties.json").ToObject<TreeGenProperties>();
        var treeGens = trees.TreeGens.Concat(trees.ShrubGens).Select(v => v.Generator.Path);
        var patches = _api.Assets.GetMany<BlockPatch[]>(_api.Logger, "worldgen/blockpatches/").Values.SelectMany(p => p);
        string[] fruit = (_api.World.GetBlock(new AssetLocation("fruittree-branch")) as Vintagestory.GameContent.BlockFruitTreeBranch)
            ?.WorldGenConds?.Select(c => c.Type).ToArray() ?? System.Array.Empty<string>();

        string[] unmatched = Catalog.Unmatched(treeGens, patches, fruit).Distinct().ToArray();
        bool active = _api.ModLoader.GetModSystem<TerrainDiffusionModSystem>()?.Climate != null;
        _api.Logger.Notification("[koppenflora] {0} flora profiles loaded; {1}. Keeping vanilla rules for {2} entries: {3}",
            Catalog.Profiles.Count, active ? "placing by Köppen climate" : "Terrain Diffusion is not generating this world, so vanilla rules apply",
            unmatched.Length, string.Join(", ", unmatched));
    }

    public override void Dispose()
    {
        _harmony?.UnpatchAll(VegetationHooks.HarmonyId);
        _harmony = null;
        VegetationHooks.Current = null;
        _api = null;
    }
}
