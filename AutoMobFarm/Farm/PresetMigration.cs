using System;
using System.Collections.Generic;
using System.Linq;
using AutoMobFarm.Data;

namespace AutoMobFarm.Farm;

/// <summary>
/// プリセットの設定の移行（版つき。1つの版の移行は1回だけ）。設定ファイルの写しは呼び出し側（Plugin）が移行の前に作る。
///  ・版 1：旧版（単品の設定）からプリセットへ。単品の設定（SelectedItemId・TargetCount・SelectedTerritory）は消さずに残す。
///  ・版 2（0.7.1.4）：帯のプリセットの名前を、アイテムタブの帯と同じ形へ（「61Lv～70Lv」→「Lv61～Lv70」）。
///    直すのは、前の版が帯から作った名前そのもの（同じ名前の2つ目以降に付く「 (2)」も）だけ。利用者が付けた名前は変えない。
/// </summary>
public static class PresetMigration
{
    public const int CurrentVersion = 2;

    /// <summary>移行する（済んでいれば何もせず null）。行ったことの説明を返す。</summary>
    public static string? Migrate(Configuration config, MobCatalog? catalog, Func<uint, string> itemName)
    {
        if (config.PresetsVersion >= CurrentVersion)
            return null;

        var notes = new List<string>();
        if (config.PresetsVersion < 1)
        {
            var note = "プリセットの形へ移行しました（単品の設定は残しています）";
            if (config.SelectedItemId != 0 && config.Presets.Count == 0)
            {
                var goal = new Goal { ItemId = config.SelectedItemId, TargetCount = Math.Max(1, config.TargetCount) };
                if (catalog != null && config.SelectedTerritory != 0)
                {
                    foreach (var name in catalog.MobsDropping(config.SelectedItemId))
                        if (catalog.PointsOf(name).Any(x => x.Territory == config.SelectedTerritory))
                            goal.Sources.Add(new SourceMobRef { NameId = name, Territory = config.SelectedTerritory });
                }

                config.Presets.Add(new Preset { Name = itemName(config.SelectedItemId), Goals = [goal], Revision = 1 });
                note += $"。選んでいた素材（{config.SelectedItemId}）のプリセットを作りました（目標 {goal.TargetCount}・モブの候補 {goal.Sources.Count}）";
            }

            notes.Add(note);
            config.PresetsVersion = 1;
        }

        if (config.PresetsVersion < 2)
        {
            var renamed = RenameBandPresets(config.Presets);
            notes.Add(renamed.Count == 0
                ? "帯のプリセットの名前を直す移行：前の形の名前はありませんでした"
                : $"帯のプリセットの名前をアイテムタブの帯と同じ形にしました（{string.Join("・", renamed.Select(r => $"{r.From}→{r.To}"))}）");
            config.PresetsVersion = 2;
        }

        return string.Join("。", notes);
    }

    /// <summary>
    /// 前の版が帯から作った名前（例：「61Lv～70Lv」「61Lv～70Lv (2)」）を、アイテムタブの帯と同じ形（「Lv61～Lv70」「Lv61～Lv70 (2)」）へ直す。
    /// 直した先の名前がほかのプリセットと重なるときは「 (2)」「 (3)」…を付ける。直した組（前・後）を返す。
    /// </summary>
    public static List<(string From, string To)> RenameBandPresets(List<Preset> presets)
    {
        var renamed = new List<(string, string)>();
        foreach (var p in presets)
        {
            var band = CatalogQuery.Bands.FirstOrDefault(b => p.Name == b.OldPresetName || IsNumberedCopy(p.Name, b.OldPresetName));
            if (band == null)
                continue;
            var to = band.Name + p.Name[band.OldPresetName.Length..]; // 「 (2)」などはそのまま付け直す
            for (var i = 2; presets.Any(o => !ReferenceEquals(o, p) && o.Name == to); i++)
                to = $"{band.Name} ({i})";
            renamed.Add((p.Name, to));
            p.Name = to;
            p.Revision++;
        }

        return renamed;
    }

    /// <summary>「名前 (2)」の形か（PresetBook.UniqueName が付ける番号）。</summary>
    private static bool IsNumberedCopy(string name, string baseName)
        => name.StartsWith(baseName + " (", StringComparison.Ordinal) && name.EndsWith(')')
           && int.TryParse(name.AsSpan(baseName.Length + 2, name.Length - baseName.Length - 3), out var n) && n >= 2;
}
