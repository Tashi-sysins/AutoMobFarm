using System.Linq;
using AutoMobFarm.Farm;
using EstellUtils.UI;
using EstellUtils.UI.Widgets;

namespace AutoMobFarm.Ui;

/// <summary>画面の共通部品（RSR の控えと保持の表示）。両方の窓で同じものを使う。色は EstellUtils のテーマの状態色（NoteKind）に任せる。</summary>
public static class RsrPanel
{
    /// <summary>RSR の控え・保持・復元の表示と「RSR を元に戻す」（安全停止では自動で戻さない）。</summary>
    public static void Draw(PluginParts p)
    {
        var rc = p.RsrControl;
        var r = p.Runner;
        switch (rc.Phase)
        {
            case RsrPhase.Held:
                EUi.WrapColored(rc.RestoreProblem ?? rc.Note ?? "RSR は Off のまま元の設定の控えを保持しています", NoteKind.Warning);
                if (rc.Backup is { } b)
                    EUi.Muted($"控え：モード {RsrController.ModeName(b.Mode)}・範囲攻撃 {RsrController.AoeName(b.Aoe)}・"
                              + string.Join("・", b.Bools.Select(kv => $"{kv.Key} {kv.Value}")), wrap: true);
                if (EUi.Button("RSR を元に戻す##rsrRestore", disabled: r.Running || p.Goals.Active)
                        .TipIf(r.Running || p.Goals.Active, "収集の実行中は押せません（止めてから押してください）"))
                    rc.Restore(p.World.Now);
                EUi.Muted("（周りに敵がいないことを確かめてから押してください。元が Auto だと近くの敵を攻撃し始めます）", wrap: true);
                break;
            case RsrPhase.Restoring:
                EUi.Muted(rc.Note ?? "RSR を元の設定へ戻しています", wrap: true);
                break;
            case RsrPhase.Controlling when !r.Running:
                EUi.Muted("RSR は Off（次の品を集めるために、元の設定の控えを持っています）", wrap: true);
                break;
            case RsrPhase.Idle when rc.Note != null && !r.Running:
                EUi.Muted(rc.Note, wrap: true);
                break;
        }
    }
}
