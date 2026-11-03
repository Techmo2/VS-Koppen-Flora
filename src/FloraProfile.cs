using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using VSTerrainDiffusion.Api;

namespace KoppenFlora;

/// <summary>
/// One plant's climate envelope, from where it grows on Earth: the Köppen classes it is found in and
/// ranges of the climate figures, each tapering to nothing past its limits. Read from
/// <c>config/flora/*.json</c> in any mod's assets.
///
/// A range is four numbers <c>[min, lowOptimum, highOptimum, max]</c>: the plant is absent at or
/// below <c>min</c>, rises smoothly to full strength at <c>lowOptimum</c>, holds there up to
/// <c>highOptimum</c> and fades out by <c>max</c>. Either end may be null for no limit on that side.
/// </summary>
public sealed class FloraProfile
{
    /// <summary>Unique name; a later file with the same id replaces this one.</summary>
    [JsonProperty("id")] public string Id;

    /// <summary>The real species this stands for, for people reading the file.</summary>
    [JsonProperty("species")] public string Species;

    [JsonProperty("notes")] public string Notes;

    /// <summary>Tree or shrub generator names from <c>treengenproperties.json</c>.</summary>
    [JsonProperty("treegens")] public string[] TreeGens;

    /// <summary>Block code wildcards; a block patch whose codes match one is governed by this profile.</summary>
    [JsonProperty("patches")] public string[] Patches;

    /// <summary>Wild fruit tree types (<c>fruittree-branch</c>'s worldgen list).</summary>
    [JsonProperty("fruitTrees")] public string[] FruitTrees;

    /// <summary>
    /// <c>replace</c> (default): the profile decides instead of vanilla's temperature and rain
    /// limits. <c>multiply</c>: vanilla's limits stay and the profile thins what they allow, for
    /// generic plants (grass, mushrooms) that vanilla spreads over several patches by climate.
    /// </summary>
    [JsonProperty("mode")] public string Mode = "replace";

    /// <summary>
    /// Multiplies vanilla's weight for this tree in the mix where it competes with others: below 1
    /// for something vanilla weights heavily but that is a minor part of real forests.
    /// </summary>
    [JsonProperty("weight")] public float Weight = 1f;

    /// <summary>How well the plant does in each Köppen class, 0 to 1. A class not listed is 0.</summary>
    [JsonProperty("koppen")] public Dictionary<string, float> Koppen;

    /// <summary>Annual mean temperature, °C.</summary>
    [JsonProperty("temperatureC")] public float?[] TemperatureC;

    /// <summary>Annual precipitation, mm.</summary>
    [JsonProperty("precipitationMm")] public float?[] PrecipitationMm;

    /// <summary>Mean temperature of the coldest month, °C: frost tolerance, and winter chill for fruit trees.</summary>
    [JsonProperty("coldestMonthC")] public float?[] ColdestMonthC;

    /// <summary>Mean temperature of the warmest month, °C: the summer heat the plant needs or bears.</summary>
    [JsonProperty("warmestMonthC")] public float?[] WarmestMonthC;

    /// <summary>
    /// The usual winter night: coldest month mean minus half the diurnal range, °C. Keeps frost-tender
    /// plants out of places whose winter nights freeze even when the monthly mean does not.
    /// </summary>
    [JsonProperty("winterNightC")] public float?[] WinterNightC;

    /// <summary>Rain in the driest month, mm: tolerance of a dry season.</summary>
    [JsonProperty("driestMonthMm")] public float?[] DriestMonthMm;

    /// <summary>Mean diurnal temperature range, °C.</summary>
    [JsonProperty("diurnalRangeC")] public float?[] DiurnalRangeC;

    [JsonIgnore] public bool Multiplies => string.Equals(Mode, "multiply", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore] private float[] _koppenWeights;

    /// <summary>Checks the file's values and indexes the Köppen weights. Throws with a readable message.</summary>
    public void Prepare()
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new FormatException("a profile has no id");
        if (!Multiplies && !string.Equals(Mode, "replace", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"'{Id}': mode must be replace or multiply, not '{Mode}'");

        if (Weight < 0f) throw new FormatException($"'{Id}': weight must not be negative");
        if (Koppen != null)
        {
            _koppenWeights = new float[VSTerrainDiffusion.Api.Koppen.Count];
            foreach (KeyValuePair<string, float> entry in Koppen)
            {
                int index = VSTerrainDiffusion.Api.Koppen.IndexOf(entry.Key);
                if (index < 0) throw new FormatException($"'{Id}': '{entry.Key}' is not a Köppen class");
                if (entry.Value < 0f || entry.Value > 1f) throw new FormatException($"'{Id}': weight for {entry.Key} must be 0 to 1");
                _koppenWeights[index] = entry.Value;
            }
        }

        foreach ((string name, float?[] range) in new[]
                 {
                     ("temperatureC", TemperatureC), ("precipitationMm", PrecipitationMm), ("coldestMonthC", ColdestMonthC),
                     ("warmestMonthC", WarmestMonthC), ("winterNightC", WinterNightC), ("driestMonthMm", DriestMonthMm),
                     ("diurnalRangeC", DiurnalRangeC)
                 })
        {
            if (range == null) continue;
            if (range.Length != 4) throw new FormatException($"'{Id}': {name} needs four values [min, lowOptimum, highOptimum, max]");
            if (range[0].HasValue != range[1].HasValue || range[2].HasValue != range[3].HasValue)
                throw new FormatException($"'{Id}': {name} needs both values of a side, or neither");
            float last = float.MinValue;
            foreach (float? v in range)
            {
                if (!v.HasValue) continue;
                if (v.Value < last) throw new FormatException($"'{Id}': {name} must not decrease");
                last = v.Value;
            }
        }
    }

    /// <summary>How well the plant does at a place, 0 to 1: the Köppen fit times every range.</summary>
    public float Suitability(in ClimateSnapshot c)
    {
        if (c.IsWater) return 0f;

        float score = 1f;
        if (_koppenWeights != null)
        {
            score = 0f;
            for (int k = 0; k < _koppenWeights.Length; k++) score += _koppenWeights[k] * c.Memberships[k];
            if (score <= 0f) return 0f;
        }

        score *= Range(TemperatureC, c.TemperatureC);
        score *= Range(PrecipitationMm, c.PrecipitationMm);
        score *= Range(ColdestMonthC, c.ColdestMonthC);
        score *= Range(WarmestMonthC, c.WarmestMonthC);
        score *= Range(WinterNightC, c.WinterNightC);
        score *= Range(DriestMonthMm, c.DriestMonthMm);
        score *= Range(DiurnalRangeC, c.DiurnalRangeC);
        return Math.Clamp(score, 0f, 1f);
    }

    private static float Range(float?[] range, float x)
    {
        if (range == null || float.IsNaN(x)) return 1f;
        float v = 1f;
        if (range[0].HasValue) v *= Ramp(x, range[0].Value, range[1].Value);
        if (range[2].HasValue) v *= 1f - Ramp(x, range[2].Value, range[3].Value);
        return v;
    }

    /// <summary>0 at or below a, 1 at or above b, smoothstep between; a step when a equals b.</summary>
    private static float Ramp(float x, float a, float b)
    {
        if (b <= a) return x >= b ? 1f : 0f;
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

/// <summary>The climate figures a <see cref="FloraProfile"/> is judged against, for one column.</summary>
public readonly struct ClimateSnapshot
{
    public readonly bool IsWater;
    public readonly float TemperatureC, PrecipitationMm, ColdestMonthC, WarmestMonthC, DriestMonthMm;
    public readonly float DiurnalRangeC, WinterNightC;

    /// <summary>Soft Köppen memberships, one per class.</summary>
    public readonly float[] Memberships;

    /// <summary>Assumed diurnal range on a world whose coarse model does not predict one.</summary>
    public const float FallbackDiurnalRangeC = 10f;

    public ClimateSnapshot(in ClimateRecord record, Koppen.Softness softness)
    {
        IsWater = record.IsWater;
        TemperatureC = record.TemperatureC;
        PrecipitationMm = record.PrecipitationMm;
        Memberships = record.KoppenMemberships(softness);

        MonthlyClimate months = record.Months();
        float coldest = float.MaxValue, warmest = float.MinValue, driest = float.MaxValue;
        for (int m = 0; m < 12; m++)
        {
            coldest = Math.Min(coldest, (float)months.TemperatureC[m]);
            warmest = Math.Max(warmest, (float)months.TemperatureC[m]);
            driest = Math.Min(driest, (float)months.PrecipitationMm[m]);
        }
        ColdestMonthC = coldest;
        WarmestMonthC = warmest;
        DriestMonthMm = driest;
        DiurnalRangeC = float.IsNaN(record.DiurnalRangeC) ? FallbackDiurnalRangeC : record.DiurnalRangeC;
        WinterNightC = coldest - DiurnalRangeC / 2f;
    }
}
