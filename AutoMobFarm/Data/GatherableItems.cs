using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AutoMobFarm.Data;

/// <summary>
/// 採集（採掘・砕岩・伐採・草刈）で採れる品をゲームの表から決める（敵を倒して集めるより、採集のほうが早い）。
/// こうした品は敵を倒して集めず、一覧・プリセット・収集から外す（呼び出し側：MobCatalog.WithoutItems・PresetBook.RemoveItems）。
/// 品の番号は列挙せず、表の性質で決める（ゲームの更新で採集点が増えた品も外れる）。採れる＝次をすべて満たす GatheringItem の行がある：
///  ・品が一致し、要るクエスト（EXDSchema の RequiredQuest）が無く、隠しでない
///    （クエストの後でしか出ない・隠しの行だけの品は、普通に採れるとは言えないので残す。埋め込みの 185 品では該当なし）
///  ・その行を、刺突漁でない採集点の基本（GatheringPointBase）が持つ。刺突漁の基本の欄は SpearfishingItem の行番号なので、番号が重なっても
///    取り違えないよう外す。刺突漁の基本＝SpearfishingNotebook が指すもの（GatheringType 5 の 64 件と一致することを確かめた。GBR は種類 4 未満で決め打ち）
///  ・その基本の採集点（GatheringPoint）が、地名のあるエリアに1つ以上ある（エリア 1 の 3080 点は地名が無く、使われていない点）
/// 釣り・刺突漁は数えない（採掘師・園芸師の採集だけ。埋め込みの品に、釣り・刺突漁で採れる品は無い）。
/// ゲーム 2026.09.15 での結果：埋め込みの 185 品のうち 14 品（錫鉱・鉄鉱・泥岩・岩塩・ボムの灰・ビーチ原木・玉繭など）。
/// GBR の決まり（種類 4 未満・エリア 0/1/901/929 を除く）でも同じ 14 品。
/// </summary>
public static class GatherableItems
{
    /// <summary>GatheringItem の1行（ゲームに触らない形。試験では実物の表から作る）。</summary>
    public readonly record struct ItemRow(uint RowId, uint ItemId, uint RequiredQuest, bool Hidden);

    /// <summary>GatheringPointBase の1行（Slots＝品の欄。採集の基本なら GatheringItem の行番号、刺突漁の基本なら SpearfishingItem の行番号）。</summary>
    public readonly record struct BaseRow(uint RowId, IReadOnlyList<uint> Slots);

    /// <summary>GatheringPoint の1行（どの基本の点が、どのエリアにあるか）。</summary>
    public readonly record struct PointRow(uint BaseId, uint Territory);

    /// <summary>採集で採れる品の番号の集合（上の決まり）。</summary>
    public static HashSet<uint> Find(IEnumerable<ItemRow> items, IEnumerable<BaseRow> bases, IEnumerable<PointRow> points,
        IReadOnlySet<uint> spearfishingBases, Func<uint, bool> territoryHasPlace)
    {
        var placedBases = points.Where(p => p.BaseId != 0 && territoryHasPlace(p.Territory)).Select(p => p.BaseId).ToHashSet();
        var onNodes = new HashSet<uint>(); // 地名のあるエリアに点がある、刺突漁でない基本が持つ GatheringItem の行
        foreach (var b in bases)
        {
            if (spearfishingBases.Contains(b.RowId) || !placedBases.Contains(b.RowId))
                continue;
            foreach (var slot in b.Slots)
                if (slot != 0)
                    onNodes.Add(slot);
        }

        return items.Where(i => i.ItemId != 0 && i.RequiredQuest == 0 && !i.Hidden && onNodes.Contains(i.RowId))
            .Select(i => i.ItemId).ToHashSet();
    }

    /// <summary>
    /// ゲームの表から（Dalamud の IDataManager でも、試験の Lumina の GameData でも同じ表の型）。
    /// 要るクエストは、安定版の型（Lumina.Excel.Sheets）では Unknown0 という名前の欄（EXDSchema の並びで RequiredQuest。GBR の調査の道具と同じ読み方）。
    /// 型の欄の名前が変わると、ここが読めずに例外になる（呼び出し側は外さずに続ける）。
    /// </summary>
    public static HashSet<uint> FromSheets(ExcelSheet<GatheringItem> items, ExcelSheet<GatheringPointBase> bases, ExcelSheet<GatheringPoint> points,
        ExcelSheet<SpearfishingNotebook> spearfishing, ExcelSheet<TerritoryType> territories)
        => Find(
            items.Select(g => new ItemRow(g.RowId, g.Item.RowId, g.Unknown0, g.IsHidden)),
            bases.Select(b => new BaseRow(b.RowId, b.Item.Select(r => r.RowId).ToList())),
            points.Select(p => new PointRow(p.GatheringPointBase.RowId, p.TerritoryType.RowId)),
            spearfishing.Select(n => n.GatheringPointBase.RowId).Where(r => r != 0).ToHashSet(),
            t => t != 0 && territories.TryGetRow(t, out var row) && row.PlaceName.RowId != 0);

    /// <summary>いま動いているゲームの表から。</summary>
    public static HashSet<uint> FromGame()
        => FromSheets(Svc.Data.GetExcelSheet<GatheringItem>(), Svc.Data.GetExcelSheet<GatheringPointBase>(), Svc.Data.GetExcelSheet<GatheringPoint>(),
            Svc.Data.GetExcelSheet<SpearfishingNotebook>(), Svc.Data.GetExcelSheet<TerritoryType>());
}
