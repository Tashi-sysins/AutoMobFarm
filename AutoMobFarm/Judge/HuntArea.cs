using System.Collections.Generic;
using System.Linq;
using System.Numerics;
namespace AutoMobFarm.Judge;

/// <summary>狩り場の点。Y が NaN なら高さ未確定（地図待ちで決める）。Anchor＝狩り場の範囲を決める点（カタログ・クリックした地点）。
/// 見つけた位置（Anchor=false）は巡回の点の高さと重みにだけ使い、範囲は広げない（S6）。</summary>
public sealed record AreaPoint(float X, float Y, float Z, int Weight, bool Anchor, string Source);

/// <summary>
/// 狩り場（1エリア・1モブ名）。攻撃してよいのは、名前が同じで、基本 ID が BaseIds に入り（空なら問わない）、
/// どれかの Anchor の点から水平 RadiusH 以内にいる個体（高さは見ない：カタログに高さが無い）。
/// </summary>
public sealed record HuntArea(uint Territory, uint NameId, IReadOnlySet<uint> BaseIds, IReadOnlyList<AreaPoint> Points, float RadiusH = HuntArea.DefaultRadiusH)
{
    /// <summary>S4：Teamcraft の升目（地図座標 1.0 ≒ 50m）の中心から角まで約 35m＋歩き回り。</summary>
    public const float DefaultRadiusH = 60f;

    public string Key => $"{this.Territory}:{this.NameId}";

    public bool AcceptsBase(uint baseId) => this.BaseIds.Count == 0 || this.BaseIds.Contains(baseId);

    public bool Contains(Vector3 p) => this.Points.Any(s => s.Anchor && Vector2.Distance(new Vector2(s.X, s.Z), new Vector2(p.X, p.Z)) <= this.RadiusH);
}
