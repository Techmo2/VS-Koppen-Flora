using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;
using VSTerrainDiffusion.Api;

namespace KoppenFlora;

/// <summary>
/// Harmony patches on vanilla's vegetation pass. Vanilla keeps its placement, forest, fertility and
/// height rules; for anything with a <see cref="FloraProfile"/>, the temperature and rain windows are
/// replaced by (or, in <c>multiply</c> mode, thinned by) the profile's suitability at the column.
///
/// None of the vanilla checks are handed a position, but every generator that calls them keeps the
/// column it is working on in a <c>tmpPos</c> field. The generator running on this thread is
/// remembered on the way in, and its <c>tmpPos</c> read when a check fires.
/// </summary>
internal static class VegetationHooks
{
    public const string HarmonyId = "koppenflora.vegetation";

    /// <summary>What the hooks need, set while a world is loaded and cleared when it closes.</summary>
    internal sealed class Context
    {
        public FloraCatalog Catalog;
        public Func<TerrainClimate> Climate;
        public Koppen.Softness Softness;
        public int Seed;
    }

    internal static volatile Context Current;

    /// <summary>Patches what this game version has; returns the targets it could not find.</summary>
    public static List<string> Apply(Harmony harmony)
    {
        var missing = new List<string>();
        void Hook(Type type, string method, string prefix = null, string postfix = null, string finalizer = null)
        {
            MethodInfo target = AccessTools.Method(type, method);
            if (target == null)
            {
                missing.Add(type.Name + "." + method);
                return;
            }
            HarmonyMethod Of(string name) => name == null ? null : new HarmonyMethod(typeof(VegetationHooks), name);
            harmony.Patch(target, prefix: Of(prefix), postfix: Of(postfix), finalizer: Of(finalizer));
        }

        // The generators whose tmpPos says where a check is being made.
        foreach ((Type type, string method) in new[]
                 {
                     (typeof(GenVegetationAndPatches), "genTrees"), (typeof(GenVegetationAndPatches), "genShrubs"),
                     (typeof(GenVegetationAndPatches), "genPatches"), (typeof(ForestFloorSystem), "GenPatches")
                 })
        {
            Hook(type, method, prefix: nameof(EnterGenerator), finalizer: nameof(LeaveGenerator));
        }

        Hook(typeof(WgenTreeSupplier), nameof(WgenTreeSupplier.GetRandomGenForClimate), prefix: nameof(ChooseAmongSuitableTrees));
        Hook(typeof(BlockPatchConfig), nameof(BlockPatchConfig.IsPatchSuitableAt),
            prefix: nameof(PatchSuitableAtPrefix), postfix: nameof(PatchSuitableAtPostfix));
        Hook(typeof(BlockPatchConfig), nameof(BlockPatchConfig.IsPatchSuitableUnderTree),
            prefix: nameof(PatchSuitableUnderTreePrefix), postfix: nameof(PatchSuitableUnderTreePostfix));
        Hook(typeof(BlockFruitTreeBranch), nameof(BlockFruitTreeBranch.TryPlaceBlockForWorldGen), prefix: nameof(PlaceSuitableFruitTree));
        return missing;
    }

    // ---------------------------------------------------------------------------------------------
    // Where the check is

    [ThreadStatic] private static List<object> _generators;
    private static readonly Dictionary<Type, FieldInfo> PositionFields = new();

    private static void EnterGenerator(object __instance) => (_generators ??= new List<object>()).Add(__instance);

    private static Exception LeaveGenerator(Exception __exception)
    {
        if (_generators is { Count: > 0 }) _generators.RemoveAt(_generators.Count - 1);
        return __exception;
    }

    /// <summary>The column the innermost generator on this thread is working on.</summary>
    private static bool TryPosition(out int x, out int z)
    {
        x = z = 0;
        if (_generators is not { Count: > 0 }) return false;
        object generator = _generators[^1];
        FieldInfo field;
        lock (PositionFields)
        {
            if (!PositionFields.TryGetValue(generator.GetType(), out field))
                PositionFields[generator.GetType()] = field = AccessTools.Field(generator.GetType(), "tmpPos");
        }
        if (field?.GetValue(generator) is not BlockPos pos) return false;
        x = pos.X;
        z = pos.Z;
        return true;
    }

    /// <summary>The climate at the current column, or false to leave the decision to vanilla.</summary>
    private static bool TryClimateHere(out Context context, out ClimateSnapshot climate, out int x, out int z)
    {
        climate = default;
        x = z = 0;
        context = Current;
        TerrainClimate api = context?.Climate();
        return api != null && TryPosition(out x, out z) && ClimateSampler.TryGet(api, x, z, context.Softness, out climate);
    }

    /// <summary>A uniform number in [0, 1) fixed by world, column and profile, so a world generates the same way twice.</summary>
    private static float Roll(int seed, int x, int z, string salt)
    {
        unchecked
        {
            uint h = (uint)seed * 0x9E3779B1u ^ (uint)x * 0x85EBCA77u ^ (uint)z * 0xC2B2AE3Du ^ (uint)StringComparer.Ordinal.GetHashCode(salt);
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return (h >> 8) / 16777216f;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Trees and shrubs

    [ThreadStatic] private static Dictionary<TreeVariant, TreeVariant> _openClones;
    [ThreadStatic] private static List<TreeVariant> _candidates;
    private static readonly MethodInfo MemberwiseClone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>Below this a tree is left out of the mix rather than given a sliver of it.</summary>
    private const float MinTreeSuitability = 0.05f;

    /// <summary>
    /// Hands vanilla's picker a list in which every profiled tree has its climate windows opened up
    /// (temperature, rain, and the fertility and height that vanilla derives from them) and its weight
    /// scaled by suitability; unsuitable ones are left out. Vanilla's forest and habitat logic then runs
    /// as usual. The best suitability present also thins the attempt, so a forest fades out at the edge
    /// of its climate instead of filling up with whatever fits least badly. Vanilla's picker takes the
    /// first tree in its shuffled list once the weights left are under 0.001, so near-zero candidates
    /// must not reach it at all.
    /// </summary>
    private static bool ChooseAmongSuitableTrees(ref TreeVariant[] gens, LCGRandom rnd, ref TreeGenInstance __result)
    {
        if (!TryClimateHere(out Context context, out ClimateSnapshot climate, out _, out _)) return true;
        float best = 0f;
        bool anyProfiled = false;

        _openClones ??= new Dictionary<TreeVariant, TreeVariant>();
        List<TreeVariant> candidates = _candidates ??= new List<TreeVariant>();
        candidates.Clear();
        foreach (TreeVariant variant in gens)
        {
            ProfileSet profile = context.Catalog.ForTreeGen(variant.Generator);
            if (profile == null)
            {
                candidates.Add(variant);
                continue;
            }

            anyProfiled = true;
            float suitability = profile.Suitability(climate, out float profileWeight);
            if (suitability < MinTreeSuitability) continue;
            best = Math.Max(best, suitability);
            TreeVariant clone = OpenClone(variant, openWindows: !profile.Multiplies);
            clone.Weight = variant.Weight * suitability * profileWeight;
            candidates.Add(clone);
        }

        if (anyProfiled && (candidates.Count == 0 || rnd.NextFloat() >= Math.Max(best, UnprofiledShare(candidates, context))))
        {
            __result = null;
            return false;
        }
        gens = candidates.ToArray();
        return true;
    }

    /// <summary>1 if any unprofiled tree is in the running (vanilla decides for it), else 0.</summary>
    private static float UnprofiledShare(List<TreeVariant> candidates, Context context)
    {
        foreach (TreeVariant v in candidates)
            if (context.Catalog.ForTreeGen(v.Generator) == null) return 1f;
        return 0f;
    }

    private static TreeVariant OpenClone(TreeVariant variant, bool openWindows)
    {
        if (!_openClones.TryGetValue(variant, out TreeVariant clone))
        {
            clone = (TreeVariant)MemberwiseClone.Invoke(variant, null);
            _openClones[variant] = clone;
        }
        clone.TempMid = openWindows ? 0 : variant.TempMid;
        clone.TempRange = openWindows ? 1e6f : variant.TempRange;
        clone.RainMid = openWindows ? 0 : variant.RainMid;
        clone.RainRange = openWindows ? 1e6f : variant.RainRange;
        clone.FertMid = openWindows ? 0 : variant.FertMid;
        clone.FertRange = openWindows ? 1e6f : variant.FertRange;
        clone.HeightMid = openWindows ? 0.5f : variant.HeightMid;
        clone.HeightRange = openWindows ? 1e6f : variant.HeightRange;
        return clone;
    }

    // ---------------------------------------------------------------------------------------------
    // Block patches

    [ThreadStatic] private static ProfileSet _postfixProfile;

    /// <summary>
    /// For a replace-mode profile, vanilla's placement, biome, forest and shrub checks, then a roll
    /// against suitability. Vanilla's temperature, rain, height and fertility windows are left out:
    /// fertility is computed from rain and temperature, and height stands in for the cold of altitude,
    /// both of which the profile already weighs. Multiply-mode and unprofiled patches run vanilla and
    /// are settled in the postfix.
    /// </summary>
    private static bool PatchSuitableAtPrefix(BlockPatch patch, Block onBlock, int mapSizeY, int climate, int y,
                                              float forestRel, float shrubRel, int biome, ref bool __result)
    {
        _postfixProfile = null;
        Context context = Current;
        ProfileSet profile = context?.Catalog.ForPatch(patch);
        if (profile == null || patch.Biome != -1 || patch.Biomes != null) return true;
        if (profile.Multiplies)
        {
            _postfixProfile = profile;
            return true;
        }
        if (!TryClimateHere(out _, out ClimateSnapshot here, out int x, out int z)) return true;

        __result = false;
        if ((patch.Placement is EnumBlockPatchPlacement.NearWater or EnumBlockPatchPlacement.UnderWater) && onBlock.LiquidCode != "water") return false;
        if ((patch.Placement is EnumBlockPatchPlacement.NearSeaWater or EnumBlockPatchPlacement.UnderSeaWater) && onBlock.LiquidCode != "saltwater") return false;
        if (forestRel < patch.MinForest || forestRel > patch.MaxForest || shrubRel < patch.MinShrub || forestRel > patch.MaxShrub) return false;

        __result = Roll(context.Seed, x, z, profile.Id) < profile.Suitability(here);
        return false;
    }

    private static void PatchSuitableAtPostfix(ref bool __result)
    {
        ProfileSet profile = _postfixProfile;
        _postfixProfile = null;
        if (profile == null || !__result) return;
        if (!TryClimateHere(out Context context, out ClimateSnapshot here, out int x, out int z)) return;
        __result = Roll(context.Seed, x, z, profile.Id) < profile.Suitability(here);
    }

    private static bool PatchSuitableUnderTreePrefix(BlockPatch patch, int mapSizeY, ClimateCondition climate, int y, ref bool __result)
    {
        _postfixProfile = null;
        Context context = Current;
        ProfileSet profile = context?.Catalog.ForPatch(patch);
        if (profile == null) return true;
        if (profile.Multiplies)
        {
            _postfixProfile = profile;
            return true;
        }
        if (!TryClimateHere(out _, out ClimateSnapshot here, out int x, out int z)) return true;

        __result = false;
        if (climate.ForestDensity < patch.MinForest || climate.ForestDensity > patch.MaxForest) return false;

        __result = Roll(context.Seed, x, z, profile.Id) < profile.Suitability(here);
        return false;
    }

    private static void PatchSuitableUnderTreePostfix(ref bool __result) => PatchSuitableAtPostfix(ref __result);

    // ---------------------------------------------------------------------------------------------
    // Wild fruit trees

    /// <summary>
    /// Vanilla tries the fruit types from a random start and takes the first whose window fits and
    /// whose chance rolls. Profiled types roll their suitability instead of the window.
    /// </summary>
    private static bool PlaceSuitableFruitTree(BlockFruitTreeBranch __instance, IBlockAccessor blockAccessor, BlockPos pos,
                                               IRandom worldgenRandom, ref bool __result)
    {
        Context context = Current;
        TerrainClimate api = context?.Climate();
        FruitTreeWorldGenConds[] conds = __instance.WorldGenConds;
        if (api == null || conds == null || conds.Length == 0) return true;

        __result = false;
        if (blockAccessor.GetBlock(pos.DownCopy()).Fertility <= 20) return false;
        if (!ClimateSampler.TryGet(api, pos.X, pos.Z, context.Softness, out ClimateSnapshot here)) return true;

        ClimateCondition vanilla = blockAccessor.GetClimateAt(pos, EnumGetClimateMode.WorldGenValues);
        int start = worldgenRandom.NextInt(conds.Length);
        for (int i = 0; i < conds.Length; i++)
        {
            FruitTreeWorldGenConds cond = conds[(i + start) % conds.Length];
            ProfileSet profile = context.Catalog.ForFruitTree(cond.Type);
            bool fits = profile != null && !profile.Multiplies
                ? worldgenRandom.NextFloat() < profile.Suitability(here)
                : vanilla != null && cond.MinTemp <= vanilla.Temperature && cond.MaxTemp >= vanilla.Temperature
                  && cond.MinRain <= vanilla.Rainfall && cond.MaxRain >= vanilla.Rainfall
                  && (profile == null || worldgenRandom.NextFloat() < profile.Suitability(here));
            if (!fits || worldgenRandom.NextFloat() > cond.Chance) continue;

            blockAccessor.SetBlock(__instance.BlockId, pos);
            blockAccessor.SpawnBlockEntity(__instance.EntityClass, pos);
            if (blockAccessor.GetBlockEntity(pos) is BlockEntityFruitTreeBranch be)
            {
                be.TreeType = cond.Type;
                be.FastForwardGrowth = 0.2f + 0.8f * worldgenRandom.NextFloat();
            }
            __result = true;
            return false;
        }
        return false;
    }
}
