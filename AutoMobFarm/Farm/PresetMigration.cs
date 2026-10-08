using System;
using System.Linq;
using AutoMobFarm.Data;

namespace AutoMobFarm.Farm;

/// <summary>
/// 旧版（単品の設定）からプリセットへの移行（1回だけ。版つき）。単品の設定（SelectedItemId・TargetCount・SelectedTerritory）は消さずに残す。
/// 設定ファイルの写しは呼び出し側（Plugin）が移行の前に作る。
/// </summary>
public static class PresetMigration
{
    public const int CurrentVersion = 1;

    /// <summary>移行する（済んでいれば何もせず null）。行ったことの説明を返す。</summary>
    public static string? Migrate(Configuration config, MobCatalog? catalog, Func<uint, string> itemName)
    {
        if (config.PresetsVersion >= CurrentVersion)
            return null;

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

        config.PresetsVersion = CurrentVersion;
        return note;
    }
}
