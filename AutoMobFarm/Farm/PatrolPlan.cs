using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoMobFarm.Judge;

namespace AutoMobFarm.Farm;

/// <summary>巡回の点（群れ）。高さは地図・当たり判定か見つけた位置の値。</summary>
public sealed record PatrolSpot(Vector3 Center, int Seen, IReadOnlyList<uint> NameIds);

/// <summary>
/// 巡回の計画（どこを回るか）。こちらから仕掛けてよい個体かの判定（TargetJudge）とは別にしてある。
///
/// 回る点は、狩り場の点のうち高さが決まった物だけ（カタログの点は地図待ちで床の高さを入れる。決まらない点・座標の欠けた点は使わない）。
/// 見つけた位置（重み 3）はカタログの点（重み 1）より重く数え、群れの中心を本物の位置へ寄せる。
/// 点は水平 40m・高さ 10m 以内をまとめて1つの群れにする（高さの違う階は別の群れのまま）。
/// 回る順は、始めた位置から一番近い群れから一筆書き（決めたら変えない。距離だけで並べた順に回ると、長い距離を往復することがある）。
/// </summary>
public sealed class PatrolPlan
{
    public const float MergeH = 40f;
    public const float MergeV = 10f;

    public IReadOnlyList<PatrolSpot> Spots { get; }

    private PatrolPlan(List<PatrolSpot> spots) => this.Spots = spots;

    /// <summary>計画を作る。点が無ければ空。</summary>
    public static PatrolPlan Build(IEnumerable<HuntArea> areas, Vector3 start)
    {
        var clusters = new List<(Vector3 Sum, int Weight, HashSet<uint> Names)>();
        foreach (var g in areas)
        {
            foreach (var p in g.Points)
            {
                var v = new Vector3(p.X, p.Y, p.Z);
                if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
                    continue;
                var w = Math.Max(1, p.Weight);
                var idx = clusters.FindIndex(c =>
                {
                    var center = c.Sum / c.Weight;
                    return Vector2.Distance(new Vector2(center.X, center.Z), new Vector2(v.X, v.Z)) <= MergeH && MathF.Abs(center.Y - v.Y) <= MergeV;
                });
                if (idx < 0)
                {
                    clusters.Add((v * w, w, [g.NameId]));
                }
                else
                {
                    var c = clusters[idx];
                    c.Names.Add(g.NameId);
                    clusters[idx] = (c.Sum + (v * w), c.Weight + w, c.Names);
                }
            }
        }

        var spots = clusters.Select(c => new PatrolSpot(c.Sum / c.Weight, c.Weight, [.. c.Names.OrderBy(n => n)])).ToList();

        // 一筆書き：始めた位置から一番近い点、そこから一番近い未訪の点…
        var ordered = new List<PatrolSpot>();
        var at = new Vector2(start.X, start.Z);
        while (spots.Count > 0)
        {
            var from = at;
            var next = spots.MinBy(s => Vector2.Distance(new Vector2(s.Center.X, s.Center.Z), from))!;
            ordered.Add(next);
            spots.Remove(next);
            at = new Vector2(next.Center.X, next.Center.Z);
        }

        return new PatrolPlan(ordered);
    }
}
