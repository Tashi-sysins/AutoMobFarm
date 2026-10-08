using System.Collections.Generic;
using System.Linq;

namespace AutoMobFarm.Data;

/// <summary>表示する文と並べ替えの値。ゲームに触らず、画面と試験で同じ規則を使う。</summary>
public static class CatalogDisplay
{
    public static string ExVersionName(byte id, string fallback) => id switch
    {
        0 => "新生",
        1 => "蒼天",
        2 => "紅蓮",
        3 => "漆黒",
        4 => "暁月",
        5 => "黄金",
        _ => fallback,
    };

    public static bool LevelBlocksLearning(IReadOnlyList<int> levels)
        => levels.Count > 0 && levels.All(level => level >= 71);

    public static int BlueMagicOrder(CatalogMob? mob, IReadOnlyList<int> levels)
        => mob == null || mob.BlueMagic.Count == 0 || LevelBlocksLearning(levels)
            ? int.MaxValue : mob.BlueMagic.Min(b => b.No);

    public static string BlueMagicText(CatalogMob? mob, IReadOnlyList<int> levels)
    {
        if (mob == null || mob.BlueMagic.Count == 0 || LevelBlocksLearning(levels))
            return "ー";
        return string.Join("・", mob.BlueMagic.OrderBy(b => b.No).Select(b => $"{b.No} {b.Action}"));
    }

    public static string BlueMagicTooltip(CatalogMob? mob, IReadOnlyList<int> levels)
    {
        if (mob == null || mob.BlueMagic.Count == 0)
            return string.Empty;
        if (LevelBlocksLearning(levels))
            return "Lv71 以上の敵からはラーニングできません";
        return "ゲームエイトの青魔法一覧（6.45対応・2026-10-08 取得）" + (levels.Count == 0 ? "\nレベル不明" : string.Empty);
    }

    public static int CaptureOrder(CatalogMob? mob)
        => mob == null || mob.Capture.Count == 0 ? int.MaxValue : mob.Capture.Min(b => b.No);

    public static string CaptureText(CatalogMob? mob)
        => mob == null || mob.Capture.Count == 0 ? "ー"
            : string.Join("・", mob.Capture.OrderBy(b => b.No).Select(b => $"{b.No} {b.Species}").Distinct());

    public static string CaptureTooltip(CatalogMob? mob)
        => mob == null || mob.Capture.Count == 0 ? string.Empty
            : string.Join("\n", mob.Capture.OrderBy(b => b.No).Select(b => b.Where
                + (b.LevelMin is { } lo && b.LevelMax is { } hi ? $" Lv{lo}〜{hi}" : string.Empty)))
              + "\n図鑑の番号＝ゲームのデータ・対象の敵＝BeastHelper の表";
}
