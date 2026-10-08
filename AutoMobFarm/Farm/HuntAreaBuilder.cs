using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoMobFarm.Data;
using AutoMobFarm.Judge;
namespace AutoMobFarm.Farm;

/// <summary>
/// カタログの地点（高さなし）と見つけた位置（高さつき）から狩り場（<see cref="HuntArea"/>）を作る（複数品の計画・その場の討伐の両方で使う）。
/// カタログの点が範囲を決め（Anchor）、30m 以内に見つけた位置があればその高さを写す。見つけた位置は範囲の中の物だけを、重い点として足す（範囲は広げない：S6）。
/// </summary>
public static class HuntAreaBuilder
{
    /// <summary>見つけた位置の高さを、近くのカタログの点へ写す水平の距離。</summary>
    public const float FoundHeightReach = 30f;
    public const int CatalogWeight = 1;
    public const int FoundWeight = 3;

    /// <summary>カタログの地点（同じエリア・同じ名前）と見つけた位置から狩り場を作る。カタログの地点が無ければ null。</summary>
    public static HuntArea? Build(uint territory, uint nameId, IEnumerable<CatalogPoint> catalogPoints, IEnumerable<FoundSpot> found, float radiusH = HuntArea.DefaultRadiusH)
    {
        var cps = catalogPoints.Where(p => p.Territory == territory && p.NameId == nameId && p.WorldX != null && p.WorldZ != null).ToList();
        if (cps.Count == 0)
            return null;
        var fs = found.Where(f => f.Territory == territory && f.NameId == nameId).ToList();
        var bases = cps.Select(p => p.BaseId).OfType<uint>().Where(b => b != 0).ToHashSet();
        var points = new List<AreaPoint>();
        foreach (var cp in cps)
        {
            var (x, z) = (cp.WorldX!.Value, cp.WorldZ!.Value);
            var near = fs.Where(f => Flat(f.X, f.Z, x, z) <= FoundHeightReach).OrderBy(f => Flat(f.X, f.Z, x, z)).FirstOrDefault();
            points.Add(new AreaPoint(x, near?.Y ?? float.NaN, z, CatalogWeight, Anchor: true, $"catalog:{cp.Key}"));
        }

        // 見つけた位置は、範囲の中の物だけを巡回の点に足す（範囲は広げない：S6）
        foreach (var f in fs.Where(f => cps.Any(cp => Flat(f.X, f.Z, cp.WorldX!.Value, cp.WorldZ!.Value) <= radiusH)))
            points.Add(new AreaPoint(f.X, f.Y, f.Z, FoundWeight, Anchor: false, "found"));
        return new HuntArea(territory, nameId, bases, points, radiusH);
    }
    private static float Flat(float x, float z, float a, float b) => Vector2.Distance(new(x, z), new(a, b));
}
