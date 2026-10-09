using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoMobFarm.Data;

/// <summary>戦闘レベルの帯（10 ずつ）。</summary>
public sealed record LevelBand(int Min, int Max)
{
    /// <summary>
    /// 画面の左の一覧の名前（例：Lv91～Lv100）。帯から作るプリセットの名前もこれ（アイテムタブの帯と同じ名前にそろえる。
    /// 0.7.1.3 までの「91Lv～100Lv」の形の名前のプリセットは PresetMigration が直す）。
    /// </summary>
    public string Name => $"Lv{this.Min}～Lv{this.Max}";

    /// <summary>0.7.1.3 までの帯のプリセットの名前（例：91Lv～100Lv）。前の名前を今の名前へ直すときだけ使う。</summary>
    public string OldPresetName => $"{this.Min}Lv～{this.Max}Lv";

    public bool Contains(int level) => level >= this.Min && level <= this.Max;
}

/// <summary>その帯の1地点（どのモブのどの地点が、帯に入るどのレベルか）。</summary>
public sealed record BandEntry(uint NameId, CatalogPoint Point, IReadOnlyList<int> LevelsInBand, bool Observed = false);

/// <summary>その帯の1品（帯に入るドロップ元の最低レベル・全部の地点）。</summary>
public sealed record BandItem(uint ItemId, int MinLevel, IReadOnlyList<BandEntry> Entries);

/// <summary>モブの表の1行（敵の種類 × 出現エリア。地点は全部持つ）。</summary>
public sealed record MobRow(uint NameId, uint Territory, IReadOnlyList<CatalogPoint> Points, IReadOnlyList<int> Levels, IReadOnlyList<uint> Items, int ReviewPoints, byte? ExVersion)
{
    /// <summary>表示の集合が Garland の参考値だけなら印を付ける。</summary>
    public bool ReferenceLevels { get; init; }

    public bool ObservedLevels { get; init; }

    /// <summary>青魔法の判定は参考も含む、その行の分かっている値すべてで行う。</summary>
    public IReadOnlyList<int> LearningLevels { get; init; } = Levels;

    /// <summary>すべての地点が要確認（クエストの置き場所と重なる・FATE 名と同名など）。</summary>
    public bool AllReview => this.ReviewPoints == this.Points.Count;
}

/// <summary>
/// カタログから画面の表を組み立てる（ゲームに触らない。試験で同じものを動かす）。
///  ・レベルは地点ごとの観測値の集合。帯は「その帯に入る観測値がある地点」だけで決め、最小と最大の間を補わない。
///  ・同じ名前でも、地点ごとに別の帯へ載る（例：Lv90 の地点は 81～90、Lv91 の地点は 91～100）。
///  ・レベル不明の地点は 10 の帯に入れず、別に数える（11 番目の帯にしない）。
/// </summary>
public static class CatalogQuery
{
    /// <summary>固定の 10 帯（高い順：画面の左の一覧の順）。</summary>
    public static readonly IReadOnlyList<LevelBand> Bands = Enumerable.Range(0, 10).Select(i => new LevelBand(91 - (i * 10), 100 - (i * 10))).ToList();

    /// <summary>
    /// その帯の品（ItemId でまとめ、帯に入るドロップ元の最低レベルの低い順。同じなら名前、ItemId の順）。各品の中に帯の地点をすべて入れる。
    /// </summary>
    public static List<BandItem> ItemsInBand(MobCatalog c, LevelBand band, Func<uint, string> itemName,
        Func<uint, uint, IReadOnlyList<int>>? seenLevels = null)
    {
        var entries = new Dictionary<uint, List<BandEntry>>();
        foreach (var mob in c.Mobs)
        {
            foreach (var p in c.PointsOf(mob.NameId))
            {
                var (effective, observed) = PointLevels(p, seenLevels);
                var inBand = effective.Where(band.Contains).ToList();
                if (inBand.Count == 0)
                    continue;
                foreach (var item in mob.Items)
                {
                    if (!entries.TryGetValue(item, out var list))
                        entries[item] = list = [];
                    list.Add(new BandEntry(mob.NameId, p, inBand, observed));
                }
            }
        }

        return entries
            .Select(kv => new BandItem(kv.Key, kv.Value.Min(e => e.LevelsInBand.Min()),
                kv.Value.OrderBy(e => e.LevelsInBand.Min()).ThenBy(e => e.NameId).ThenBy(e => e.Point.Territory).ThenBy(e => e.Point.Key, StringComparer.Ordinal).ToList()))
            .OrderBy(i => i.MinLevel)
            .ThenBy(i => itemName(i.ItemId), StringComparer.Ordinal)
            .ThenBy(i => i.ItemId)
            .ToList();
    }

    /// <summary>レベル不明の地点（品を落とすモブのもの）。10 の帯には入れない。</summary>
    public static List<BandEntry> UnknownLevelEntries(MobCatalog c, Func<uint, uint, IReadOnlyList<int>>? seenLevels = null)
        => c.Mobs.Where(m => m.Items.Count > 0)
            .SelectMany(m => c.PointsOf(m.NameId).Where(p => PointLevels(p, seenLevels).Levels.Count == 0).Select(p => new BandEntry(m.NameId, p, [])))
            .ToList();

    /// <summary>レベル不明の地点を品ごとにまとめたもの（品の名前、ItemId の順。最低レベルは無いので 0）。</summary>
    public static List<BandItem> UnknownLevelItems(MobCatalog c, Func<uint, string> itemName,
        Func<uint, uint, IReadOnlyList<int>>? seenLevels = null)
        => UnknownLevelEntries(c, seenLevels)
            .SelectMany(e => (c.Mob(e.NameId)?.Items ?? []).Select(i => (Item: i, Entry: e)))
            .GroupBy(x => x.Item)
            .Select(g => new BandItem(g.Key, 0, g.Select(x => x.Entry).OrderBy(e => e.NameId).ThenBy(e => e.Point.Key, StringComparer.Ordinal).ToList()))
            .OrderBy(i => itemName(i.ItemId), StringComparer.Ordinal)
            .ThenBy(i => i.ItemId)
            .ToList();

    /// <summary>同じ敵は1行。地点数、元の地点数、エリア番号の順で代表を選ぶ。品の表の候補は減らさない。</summary>
    public static List<MobRow> MobRows(MobCatalog c, Func<uint, uint, IReadOnlyList<int>>? seenLevels = null)
        => c.Mobs
            .SelectMany(m => c.PointsOf(m.NameId).GroupBy(p => p.Territory)
                .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Sum(p => p.Count)).ThenBy(g => g.Key)
                .Take(1).Select(g => new MobRow(
                m.NameId, g.Key,
                g.OrderBy(p => p.NeedsReview).ThenByDescending(p => p.Count).ThenBy(p => p.Key, StringComparer.Ordinal).ToList(),
                DisplayLevels(g),
                m.Items,
                g.Count(p => p.NeedsReview),
                c.ExVersion(g.Key))
                {
                    ReferenceLevels = g.Any(p => p.Levels.Count > 0) && !g.Any(p => p.Levels.Count > 0 && !p.ReferenceLevels),
                    LearningLevels = g.SelectMany(p => p.Levels).Distinct().OrderBy(l => l).ToList(),
                }))
            .Select(row => Overlay(row, seenLevels))
            .ToList();

    /// <summary>
    /// 品の代表の敵（アイテムの表・自動討伐の表に1行だけ出す敵と座標）。モブの表の行そのものを返すので、
    /// 座標（Points[0]）・エリア・レベルは必ずモブの表と同じになる。
    ///  ・候補（その品を落とす敵×エリア）のうち、モブの表の代表エリアが候補に入っている敵を先にする（帯の外のエリアを出さないため）。
    ///  ・同じ中は、モブの表の代表の選び方と同じ並べ方：地点数の多い順 → 元の地点数の合計の多い順 → 名前 ID の小さい順。
    ///  ・どの敵の代表エリアも候補に入らなければ、候補の敵のモブの表の行から同じ順で選ぶ。モブの表に無い敵だけなら null。
    /// </summary>
    public static MobRow? Representative(IEnumerable<(uint NameId, uint Territory)> candidates, IReadOnlyDictionary<uint, MobRow> rowsByName)
    {
        var wanted = candidates.ToHashSet();
        return wanted.Select(c => c.NameId).Distinct()
            .Select(n => rowsByName.GetValueOrDefault(n))
            .OfType<MobRow>()
            .OrderByDescending(r => wanted.Contains((r.NameId, r.Territory)))
            .ThenByDescending(r => r.Points.Count)
            .ThenByDescending(r => r.Points.Sum(p => p.Count))
            .ThenBy(r => r.NameId)
            .FirstOrDefault();
    }

    /// <summary>
    /// 品の選択の一覧の絞り込み（自動討伐タブの品の欄。GBR のプリセットの品の選択と同じ使い方）。
    /// 名前に絞り込みの文字を含む品を、名前の順（同じなら番号の順）で返す。空なら全部。
    /// ひらがな・カタカナ、全角・半角、大文字・小文字の違いは無視する（「ぼあ」「ﾎﾞｱ」でも「ボアの粗皮」が出る）。
    /// </summary>
    public static List<(uint Id, string Name)> FilterItems(IEnumerable<(uint Id, string Name)> items, string filter)
    {
        var f = filter.Trim();
        return items.Where(i => f.Length == 0 || NameContains(i.Name, f))
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .ThenBy(i => i.Id)
            .ToList();
    }

    private static readonly System.Globalization.CompareInfo Japanese = System.Globalization.CultureInfo.GetCultureInfo("ja-JP").CompareInfo;

    private static bool NameContains(string name, string part)
    {
        try
        {
            return Japanese.IndexOf(name, part, System.Globalization.CompareOptions.IgnoreKanaType
                | System.Globalization.CompareOptions.IgnoreWidth | System.Globalization.CompareOptions.IgnoreCase) >= 0;
        }
        catch (Exception)
        {
            // 日本語の比較が使えない環境（言語データの無い実行環境）では、大文字・小文字だけを無視して比べる
            return name.Contains(part, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>品の候補（その品を落とす敵 × 地点のあるエリア）。自動討伐の品を手で足す・選び直す・取り込むときに使う。</summary>
    public static List<Farm.SourceMobRef> SourcesOf(MobCatalog c, uint itemId)
        => c.MobsDropping(itemId)
            .SelectMany(n => c.PointsOf(n).Select(p => p.Territory).Distinct().Select(t => new Farm.SourceMobRef { NameId = n, Territory = t }))
            .ToList();

    private static MobRow Overlay(MobRow row, Func<uint, uint, IReadOnlyList<int>>? seenLevels)
    {
        var observed = seenLevels?.Invoke(row.Territory, row.NameId) ?? [];
        return observed.Count > 0
            ? row with { Levels = observed, LearningLevels = observed, ReferenceLevels = false, ObservedLevels = true }
            : row;
    }

    /// <summary>品の帯では不明・参考の地点だけを実測で埋める。既存の地点の実測値は変えない。</summary>
    public static (IReadOnlyList<int> Levels, bool Observed) PointLevels(CatalogPoint point,
        Func<uint, uint, IReadOnlyList<int>>? seenLevels = null)
    {
        var observed = seenLevels?.Invoke(point.Territory, point.NameId) ?? [];
        return (point.Levels.Count == 0 || point.ReferenceLevels) && observed.Count > 0
            ? (observed, true) : (point.Levels, false);
    }

    private static List<int> DisplayLevels(IEnumerable<CatalogPoint> points)
    {
        var known = points.Where(p => p.Levels.Count > 0).ToList();
        var measured = known.Where(p => !p.ReferenceLevels).ToList();
        return (measured.Count > 0 ? measured : known).SelectMany(p => p.Levels).Distinct().OrderBy(l => l).ToList();
    }

    /// <summary>品を落とすが、カタログに地点の無いモブ（出現記録なし）。</summary>
    public static List<uint> MobsWithoutPoints(MobCatalog c) => c.Mobs.Where(m => c.PointsOf(m.NameId).Count == 0).Select(m => m.NameId).ToList();

    /// <summary>
    /// レベルの集合の表示（例：90・91、不明）。4 個までは範囲に縮めない（間を補わない）。5 個以上は「最小〜最大」
    /// （「4・5・6…12（9 種）」のような並びは、列の幅で切られて読めなくなるため。全部は画面の吹き出しで出す）。
    /// </summary>
    public static string LevelText(IReadOnlyCollection<int> levels)
        => levels.Count == 0 ? "不明" : levels.Count <= 4 ? string.Join("・", levels) : $"{levels.Min()}〜{levels.Max()}";

    // 見かけた記録（実測）は集合のまま出す（範囲に縮めない）。末尾に「（実測）」は付けない
    public static string LevelText(MobRow row)
        => row.ObservedLevels ? LevelText(row.Levels.ToArray())
            : LevelText(row.Levels.ToArray(), row.ReferenceLevels);

    public static string LevelText(BandEntry entry)
        => entry.Observed ? LevelText(entry.LevelsInBand.ToArray())
            : LevelText(entry.LevelsInBand.ToArray(), entry.Point.ReferenceLevels);

    /// <summary>
    /// 参考（Garland の範囲）の連続範囲だけを「20〜23」に縮める。不連続な集合の間は補わない。
    /// 末尾に「（参考）」は付けない。参考かどうかは並べ替え・青魔法の判定にだけ使う。
    /// </summary>
    public static string LevelText(IReadOnlyCollection<int> levels, bool reference)
    {
        var sorted = levels.Distinct().OrderBy(l => l).ToList();
        if (!reference || sorted.Count == 0)
            return LevelText(sorted);
        return sorted.Count > 1 && sorted[^1] - sorted[0] + 1 == sorted.Count
            ? $"{sorted[0]}〜{sorted[^1]}" : LevelText(sorted);
    }
}
