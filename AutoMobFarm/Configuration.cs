using System.Collections.Generic;
using Dalamud.Configuration;

namespace AutoMobFarm;

/// <summary>
/// 設定（pluginConfigs\AutoMobFarm.json）。名前を変えると利用者の値が消えるので、変えるときは旧名を残して移す。
/// 見つけた位置・入手ログは設定ファイルではなく、設定フォルダーの別ファイル（追記）に置く。
/// </summary>
public sealed class Configuration : IPluginConfiguration, Farm.IRsrStore
{
    public int Version { get; set; } = 1;

    /// <summary>選んだ素材（0＝未選択。アイテムの行 0 は使われないので番兵に使える）。</summary>
    public uint SelectedItemId { get; set; }

    /// <summary>目標の所持数（本人の通常の所持品の NQ＋HQ の合計）。</summary>
    public int TargetCount { get; set; } = 5;

    /// <summary>選んだ狩り場のエリア（0＝未選択）。</summary>
    public uint SelectedTerritory { get; set; }

    /// <summary>旧設定との互換のため残す。今の版では使わない。</summary>
    public bool ObservationRecording { get; set; }

    /// <summary>収集中に見かけた敵のレベルを記録し、表示へ重ねる。調査のための移動は行わない。</summary>
    public bool SeenMobRecording { get; set; } = true;

    /// <summary>起動時に画面を開くか。</summary>
    public bool OpenOnStartup { get; set; } = true;

    /// <summary>長時間の進展なしで止めるまでの分。</summary>
    public int NoProgressMinutes { get; set; } = 10;

    // ---- 自動討伐のプリセット（上の単品の設定は消さずに残す） ----

    /// <summary>プリセットの保存の版（0＝まだ移行していない。1＝今の形）。</summary>
    public int PresetsVersion { get; set; }

    public List<Farm.Preset> Presets { get; set; } = [];

    /// <summary>選んでいたプリセット（再起動で選択を戻す）。</summary>
    public string? SelectedPresetId { get; set; }

    /// <summary>単品の追加先のプリセット（「個別追加」。名前を変えても・再起動しても同じプリセットへ足すため ID で覚える）。</summary>
    public string? SinglePresetId { get; set; }

    /// <summary>モブ・アイテムの表から品を足したときの目標の所持数（画面の一括の数。初期値 1）。</summary>
    public int DefaultTargetCount { get; set; } = 1;

    // ---- RSR の戻し損ねの控え（読み込み直しても、使う前のモードを取り違えない） ----

    /// <summary>RSR の元の設定の控えがある（まだ元へ戻したと確かめていない）。</summary>
    public bool RsrHenchedPending { get; set; }

    /// <summary>安全停止で RSR を Off のまま保持している（「RSR を元に戻す」を押すまで戻さない）。</summary>
    public bool RsrHeld { get; set; }

    public byte? RsrOriginalMode { get; set; }

    /// <summary>旧版（0.1.0.0）の項目。読み捨てないために残す。今の版では使わない。</summary>
    public bool RsrAoePending { get; set; }

    public byte? RsrAoeOriginal { get; set; }

    public Dictionary<string, bool> RsrBoolOriginals { get; set; } = [];

    /// <summary>画面（EstellUtils の窓）の位置・大きさ・畳み。EstellUtils が動かし終えた時点で書き換えて保存する。</summary>
    public EstellUtils.UI.Windowing.EuWindowLayout WindowLayout { get; set; } = new();

    /// <summary>モブの表の列幅（見出しの境をつまんで変えた列だけ。ダブルクリックで戻る）。</summary>
    public EstellUtils.UI.Widgets.EuTableLayout MobColumns { get; set; } = new();

    /// <summary>アイテムの表の列幅。</summary>
    public EstellUtils.UI.Widgets.EuTableLayout ItemColumns { get; set; } = new();

    /// <summary>自動討伐の表の列幅。</summary>
    public EstellUtils.UI.Widgets.EuTableLayout GoalColumns { get; set; } = new();

    public void Save() => Svc.PluginInterface.SavePluginConfig(this);
}
