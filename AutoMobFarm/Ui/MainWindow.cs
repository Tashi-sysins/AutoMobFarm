using System;
using System.Linq;
using System.Numerics;
using AutoMobFarm.Data;
using AutoMobFarm.Farm;
using AutoMobFarm.Judge;
using EstellUtils.UI;
using EstellUtils.UI.Layout;
using EstellUtils.UI.Windowing;

namespace AutoMobFarm.Ui;

/// <summary>
/// 管理画面：環境（ジョブ・飛行・連携プラグイン・見つけた位置）と記録。
/// 画面は UI ライブラリ EstellUtils で描く。色はテーマの状態色（NoteKind）に任せる。
/// </summary>
public sealed class MainWindow : EuWindow
{
    private readonly PluginParts p;
    private string? rsrEnvCache;
    private bool? navAutoLoadCache;
    private string? bmrUnusableCache = "（まだ読んでいません）";
    private DateTime envAt = DateTime.MinValue;

    public MainWindow(PluginParts parts)
        : base("AutoMobFarm 管理（環境・記録）")
    {
        this.p = parts;
        this.Size = new Vector2(820f, 700f);
        this.MinSize = new Vector2(560f, 420f);
        this.MaxSize = new Vector2(1800f, 1600f);
        // 環境と記録はそれぞれ自分の送り領域を持つ（窓全体を送ると記録の領域の高さが決まらない）
        this.AutoScroll = false;
    }

    private Configuration Config => this.p.Config;

    public override void Draw()
    {
        using var id = EUi.PushId("amfAdmin");
        this.DrawHeader();
        EUi.Separator();

        var tabs = EUi.TabBar("##amftabs", "環境", "記録");
        if (tabs.IsSelected(0))
            this.DrawEnvTab();
        else
            this.DrawLogTab();
    }

    // ------------------------------------------------------------------
    // 上部：状態と操作
    // ------------------------------------------------------------------

    private void DrawHeader()
    {
        var r = this.p.Runner;
        using (EUi.HStack())
        {
            EUi.Label($"状態：{r.StateName}");
            EUi.Muted(r.Status);
        }

        if (r.Stop is { } s && r.State is FarmState.Stopped or FarmState.Done)
        {
            EUi.WrapColored($"{(r.State == FarmState.Done ? "完了" : "停止の理由")}：{s.Message}", r.State == FarmState.Done ? NoteKind.Success : NoteKind.Danger);
            foreach (var d in s.Details.Skip(1))
                EUi.WrapColored($"・{d}", NoteKind.Danger);
        }

        if (r.Plan is { } plan)
            EUi.Muted((plan.AdHoc ? $"{plan.ItemName}：倒した数 {r.Kills}" : $"{plan.ItemName}：はじめ {r.StartCount} → いま {r.CurrentCount} / 目標 {plan.TargetCount}・倒した数 {r.Kills}")
                      + (r.Patrol is { } pt ? $"・巡回の点 {r.SpotIndex + 1}/{pt.Spots.Count}" : string.Empty), wrap: true);
        if (r.MoveStopProblem is { } mp)
            EUi.WrapColored(mp, NoteKind.Danger);
        RsrPanel.Draw(this.p);
    }

    private void DrawEnvTab()
    {
        var w = this.p.World;
        var me = w.Player;
        var gameVersion = this.p.Catalog?.Meta.GameVersion;

        using var scroll = EUi.Scroll("##envScroll", SizeSpec.Fill);

        static void Line(string label, bool? ok, string text)
        {
            var line = $"{(ok == true ? "✓" : ok == false ? "✗" : "？")} {label}：{text}";
            if (ok == null)
                EUi.Muted(line, wrap: true);
            else
                EUi.WrapColored(line, ok == true ? NoteKind.Success : NoteKind.Danger);
        }

        Line("ジョブ", w.CombatJob, $"{w.JobName}{(w.CombatJob == false ? "（戦闘ジョブではない）" : string.Empty)}");
        Line("いまのエリア", me.Available ? true : null, me.Available ? $"{GameText.Territory(me.Territory)}（{me.Territory}・地図 {me.MapId}・インスタンス {me.Instance?.ToString() ?? "読めない"}）" : "ログインしていない");
        Line("このエリアで飛べるか", me.Available ? w.CanFlyHere : null, w.CanFlyHere ? "飛べる（風脈開放済み）" : "飛べない（歩き・騎乗だけ）");
        var (buddy, pet) = w.Summons;
        Line("チョコボ・ペット", null,
            buddy == true ? "チョコボを呼び出している（収集は止めません）" : pet == true ? "ペットを出している（収集は止めません）" : buddy == null || pet == null ? "読めない（収集は止めません）" : "出していない");
        Line("コンテンツ・リーヴ", !(me.BoundByDuty || me.Directors.Any(IdRules.LeveDirectors.Contains)),
            $"{(me.BoundByDuty ? "参加中" : "参加していない")}・ディレクター {(me.Directors.Count == 0 ? "なし" : string.Join("・", me.Directors.Select(IdRules.ContentName)))}");
        // RSR の内部と vnavmesh の設定は1秒に1回だけ読む（RSR はリフレクションで重い）
        if (DateTime.UtcNow - this.envAt > TimeSpan.FromSeconds(1))
        {
            this.envAt = DateTime.UtcNow;
            this.navAutoLoadCache = this.p.Nav.IsLoaded ? this.p.Nav.IsAutoLoad() : null;
            this.bmrUnusableCache = this.p.BossMod.Unusable;
            var rc = this.p.RsrControl;
            var mode = rc.Phase == RsrPhase.Idle ? this.p.Rsr.ReadMode() : rc.CurrentMode;
            var aoe = rc.Phase == RsrPhase.Idle ? this.p.Rsr.ReadAoe() : rc.CurrentAoe;
            this.rsrEnvCache = this.p.Rsr.IsLoaded
                ? $"読み込み済み {this.p.Rsr.LoadedVersion}・モード {(mode is { } m ? RsrController.ModeName(m) : "読めない")}・範囲攻撃 {(aoe is { } a ? RsrController.AoeName(a) : "読めない")}"
                  + rc.Phase switch
                  {
                      RsrPhase.Controlling => "・AutoMobFarm が制御中",
                      RsrPhase.Held => "・Off のまま控えを保持中",
                      RsrPhase.Restoring => "・元の設定へ戻している途中",
                      _ => string.Empty,
                  }
                : null;
        }

        Line("vnavmesh", this.p.Nav.IsLoaded, this.p.Nav.IsLoaded ? $"読み込み済み {this.p.Nav.LoadedVersion}・地図 {(this.p.Nav.IsReady() ? "準備済み" : "未準備")}" : "読み込まれていない");
        Line("vnavmesh の地図の自動読み込み", this.navAutoLoadCache, this.navAutoLoadCache switch { true => "ON", false => "OFF（収集を始めない。vnavmesh の設定で ON に）", _ => "読めない" });
        Line("RotationSolverReborn", this.p.Rsr.IsLoaded, this.rsrEnvCache ?? "読み込まれていない");
        var bmr = this.p.Bmr;
        var bmrWhy = this.bmrUnusableCache;
        Line("BossMod Reborn（範囲攻撃の回避・相手へ寄る）", bmrWhy == null ? true : null,
            bmrWhy != null ? $"{bmrWhy}（BMR なしでも収集はできます。範囲攻撃は避けません）"
            : $"読み込み済み {this.p.BossMod.LoadedVersion}"
              + (bmr.Problem is { } bp ? $"・{bp}" : bmr.Combat ? "・戦う段の足を任せています" : bmr.Leased ? "・設定を預かっています（移動中は動かさない）" : "・収集中だけ AI の設定を預かります")
              + (bmr.ExternalChanges > 0 ? $"・外から変えられて入れ直した回数 {bmr.ExternalChanges}" : string.Empty));
        var party = Svc.Party.Length;
        Line("パーティ", party <= 1 ? true : null, party <= 1 ? "組んでいない" : $"{party} 人（パーティ中の「最初に攻撃した者」の値は未確認。自分で殴った敵でも「判別できない」として手を引く可能性があります）");
        Line("Lifestream（エリア移動）", this.p.Lifestream.IsLoaded ? true : null, this.p.Lifestream.IsLoaded ? $"読み込み済み {this.p.Lifestream.LoadedVersion}" : "読み込まれていない（狩り場のエリアへは手動で移動）");
        Line("ゲームの版", string.IsNullOrEmpty(me.GameVersion) ? null : me.GameVersion == gameVersion,
            $"{me.GameVersion}（候補データ {gameVersion}）");
        EUi.Muted($"プラグイン {this.p.PluginVersion}・見分けの決まり {IdRules.Version}・設定フォルダー {this.p.ConfigDir}", wrap: true);
        EUi.Muted($"見つけた位置：{this.p.Found.Count} 件（{this.p.ConfigDir}\\found_spots.jsonl）", wrap: true);
        var recordSeen = this.Config.SeenMobRecording;
        if (EUi.Checkbox("見かけた敵を記録する", ref recordSeen))
        {
            this.Config.SeenMobRecording = recordSeen;
            this.Config.Save();
        }

        EUi.Muted($"見かけた敵：{this.p.Seen.Count} 件（{this.p.ConfigDir}\\seen_mobs.jsonl）", wrap: true);
        if (me.ReadError != null)
            EUi.WrapColored($"読めなかった値：{me.ReadError}", NoteKind.Warning);
    }

    // ------------------------------------------------------------------
    // 記録
    // ------------------------------------------------------------------

    private void DrawLogTab()
    {
        var log = Core.DebugLog.Current;
        if (log == null)
            return;
        EUi.Muted($"記録ファイル：{log.FilePath}", wrap: true);
        if (EUi.Button("新しい記録をコピー"))
            EUi.SetClipboard(string.Join("\n", log.Recent));
        using var scroll = EUi.Scroll("##logScroll", SizeSpec.Fill);
        foreach (var line in log.Recent.Reverse().Take(200))
            EUi.Paragraph(line);
    }
}
