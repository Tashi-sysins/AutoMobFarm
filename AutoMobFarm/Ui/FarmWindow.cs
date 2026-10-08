using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoMobFarm.Data;
using AutoMobFarm.Farm;
using AutoMobFarm.Ipc;
using Dalamud.Interface;
using EstellUtils.UI;
using EstellUtils.UI.Core;
using EstellUtils.UI.Layout;
using EstellUtils.UI.Render;
using EstellUtils.UI.Widgets;
using EstellUtils.UI.Windowing;

namespace AutoMobFarm.Ui;

/// <summary>
/// 主画面（GBR の画面を参考）：「モブ」「アイテム」「自動討伐」「必須プラグイン」の4タブ。
/// 環境・記録は管理画面（MainWindow）にある。座標のクリックはその場の討伐を始める。
/// 「必須プラグイン」は、足りないプラグインにだけボタンを出す。
/// 画面で分からない値を 0 や × に変えない（「不明」と出す）。
/// 画面は UI ライブラリ EstellUtils で描く。生の ImGui は使わない。
/// EstellUtils の部品（IconButton の直し・Checkbox の大きさ・中央に置かれる InputInt・Prominent／ProminentDanger・
/// SortableTableHeader・RequestPopup・Popup の送り・JustOpened・Submitted・autoFocus・SkipCell・striped・hoverable の行）を使う。hoverable の行は左ボタンを掴まない（NoCapture）ので、行の中の部品はそのまま押せる。
/// 行そのものを左クリックで選ばせる clickable は使わない（行の中の部品と競合する）。
/// </summary>
public sealed class FarmWindow : EuWindow
{
    private readonly PluginParts p;
    private readonly Action openAdmin;

    // モブの表
    private List<MobRow>? mobRows;
    private Dictionary<uint, MobRow>? rowsByName; // 名前 → モブの表の行（アイテム・自動討伐の代表の座標をモブの表とそろえる）
    private List<MobRow>? mobView;
    private string mobFilter = string.Empty;
    private string mobViewFilter = "\0";
    private TableSort mobSort = new(ColLevel);
    private TableSort mobViewSort = new(-1);
    private readonly Dictionary<uint, (MenuEntry[] Entries, uint[] Items)> addMenus = []; // 行の右クリックの項目（毎フレーム作らない）

    // アイテムの帯（-1 ＝ レベル不明）
    private int bandIndex;
    private readonly Dictionary<int, List<BandItem>> bandCache = [];
    private List<BandEntry>? unknownLevel;
    private List<BandItem>? unknownItems;
    private int seenCount = -1;

    // エーテライトの名前の控え（毎フレームゲームの表をたどらない）
    private readonly Dictionary<string, (string Text, DateTime At)> aetheryteCache = [];

    // 自動討伐
    private int bulkTarget = 1; // 「一括」の欄の数（初期値 1。変えると選んだプリセットの全部の品の目標数になる。「＋」で足す品の目標数にも使う）
    private (List<ArtisanList> Lists, string? Error) artisan = ([], null);
    private string itemFilter = string.Empty;
    private List<(uint Id, string Name)>? comboItems; // 品の選択の一覧（カタログの品＝普段から湧く敵が落とす品）
    private string? renameId;        // 名前を変えるプリセット（右クリックの「プリセット名を変更」）
    private string renameText = string.Empty;

    // 必須プラグイン（導入済みの一覧と配布元は1秒に1回だけ読む。毎フレーム Dalamud の一覧を作らせない）
    private const string RequiredTab = "必須プラグイン";
    private readonly PluginInstaller installer = new();
    private List<RequiredPlugins.State> pluginStates = [];
    private IReadOnlyList<(string Url, bool Enabled)>? pluginRepos;
    private DateTime pluginStatesAt = DateTime.MinValue;

    // モブの表の列（見出しは押すと並べ替え。もう一度で逆順）。「他 N」は座標のすぐ右の専用の列に置いて縦にそろえる
    private static readonly string[] MobHeaders = ["モブ名", "ドロップ品", "座標", string.Empty, "エーテライト", "レベル", "拡張", "ラーニング", "とらえる可否"]; // 「ラーニング」＝青魔法のラーニング（「青魔」は、ほかのプラグインの訳の置き換えで「青魔道士」と表示されることがあるため使わない）
    // 残りの幅を分け合う列（ドロップ品など）には下限を付ける（窓が狭くても潰れない。下限が無いと、ドロップ品の列が細くなって隣へはみ出す）
    private static readonly SizeSpec[] MobWidths = [190f, SizeSpec.Fill.AtLeast(200f), 220f, 52f, 150f, 80f, 60f, 140f, 150f]; // とらえる可否は「絵＋20 フライングトラップ」が入る幅
    private static readonly TableColumn[] MobBase = MobHeaders.Select((h, i) => new TableColumn(h, MobWidths[i])).ToArray();
    private const int ColName = 0, ColItems = 1, ColPoint = 2, ColMore = 3, ColAetheryte = 4, ColLevel = 5, ColEx = 6, ColBlue = 7, ColCapture = 8;

    // アイテムの表
    private static readonly TableColumn[] ItemBase = [new("アイテム名", 230f), new("座標", 270f), new("モブ名", SizeSpec.Fill.AtLeast(200f))];

    // 自動討伐の表（削除の列は無い。品の削除は行の右クリック）
    private static readonly TableColumn[] GoalBase =
        [new("有効", 44f), new("アイテム名", 270f), new("所持数 / 目標数", 280f), new("モブ名", 190f), new("座標・地域", SizeSpec.Fill.AtLeast(200f))];

    // 利用者が見出しの境をつまんで変えた列幅を反映した列定義（設定の MobColumns などへ保存）。表を描き始めるときに作り、見出しと行へ同じものを渡す
    private TableColumn[] mobCols = MobBase;
    private TableColumn[] itemCols = ItemBase;
    private TableColumn[] goalCols = GoalBase;

    private const float PaneGap = 8f;

    public FarmWindow(PluginParts parts, Action openAdmin)
        : base("AutoMobFarm（素材収集）")
    {
        this.p = parts;
        this.openAdmin = openAdmin;
        this.Size = new Vector2(1060f, 680f);
        this.MinSize = new Vector2(640f, 420f);
        this.MaxSize = new Vector2(2000f, 1600f);
        // 表はそれぞれ自分の送り領域を持つ（窓全体を送ると、表の高さが決まらない）
        this.AutoScroll = false;
    }

    private Configuration Config => this.p.Config;

    private MobCatalog? Catalog => this.p.Catalog;

    private static float RowHeight => EUi.Metrics.WidgetHeight;

    /// <summary>自動討伐の「開始・停止」と、右の表の見出しの高さ（少し大きめ。左右で同じ高さにして、下の行の位置をそろえる）。</summary>
    private static float TopRowHeight => MathF.Round(EUi.Metrics.WidgetHeight * 1.5f);

    /// <summary>
    /// 有効のチェックの一辺。「＋」（EUi.IconButton）の四角と同じ大きさにそろえる。
    /// （IconButton の一辺は EstellUtils の EUi.Buttons.cs と同じ式）。
    /// </summary>
    private static float BoxSize => MathF.Max(EUi.Metrics.WidgetHeight, EUi.Metrics.IconSize + EUi.Metrics.SpacingSm);

    /// <summary>
    /// 自動討伐の表の行の高さ。数の欄と「−」「＋」は WidgetHeight（24px）なので、行も 24px だと上下の行のボタンがすき間なく接して
    /// 被って見える。上下に 3px ずつ空ける。
    /// </summary>
    private static float GoalRowHeight => EUi.Metrics.WidgetHeight + 6f;

    /// <summary>
    /// 所持数の文字（「60 /」「読めない /」）を置く幅。文字の長さで数の欄の位置が行ごとにずれないよう、決まった幅に右寄せで置く。
    /// 一括の行も同じ幅を空けて、一括の数の欄を下の行の数の欄と同じ位置にする。
    /// </summary>
    private static float CountWidth => MathF.Ceiling(MathF.Max(TextPainter.Measure("読めない /").X, TextPainter.Measure("99999 /").X));

    public override void Draw()
    {
        using var id = EUi.PushId("amfFarm");
        this.DrawHeader();
        EUi.Separator();
        if (this.Catalog == null)
        {
            EUi.WrapColored(this.p.CatalogError ?? "モブ用カタログを読めません", NoteKind.Danger);
            return;
        }

        // 記録が増えた次の描画で、モブの行と品の帯を一緒に作り直す。
        if (this.seenCount != this.p.Seen.Count)
        {
            this.seenCount = this.p.Seen.Count;
            this.mobRows = null;
            this.rowsByName = null;
            this.mobView = null;
            this.bandCache.Clear();
            this.unknownLevel = null;
            this.unknownItems = null;
        }

        var tabs = EUi.TabBar("##amfmain", "モブ", "アイテム", "自動討伐", RequiredTab);
        switch (tabs.Selected)
        {
            case 0:
                this.DrawMobTab(this.Catalog);
                break;
            case 1:
                this.DrawItemTab(this.Catalog);
                break;
            case 2:
                this.DrawAutoTab(this.Catalog);
                break;
            default:
                this.DrawRequiredTab();
                break;
        }
    }

    // ------------------------------------------------------------------
    // 上部：状態
    // ------------------------------------------------------------------

    private void DrawHeader()
    {
        var r = this.p.Runner;
        var g = this.p.Goals;
        using (EUi.HStack())
        {
            EUi.Label($"収集：{(g.Active ? g.Status : r.StateName)}");
            if (r.Running)
                EUi.Muted(r.Status);
        }

        // 必須のプラグインが使えないと収集を始められないので、どのタブを見ていても分かるように出す（足りないときだけ）
        var lacking = RequiredPlugins.RequiredUnavailable(this.PluginStates());
        if (lacking.Count > 0)
        {
            using (EUi.HStack())
            {
                EUi.TextColored($"必須プラグインが使えません：{string.Join("・", lacking.Select(s => s.Entry.DisplayName))}", NoteKind.Danger);
                if (EUi.SmallButton($"「{RequiredTab}」を開く##reqgo"))
                    EUi.SelectTab("##amfmain", RequiredTab);
            }
        }

        if (!g.Active && g.Preset != null && !string.IsNullOrEmpty(g.Status) && g.Status != "待機")
            EUi.WrapColored(g.Status, g.Status.StartsWith("全収集完了", StringComparison.Ordinal) ? NoteKind.Success : NoteKind.Warning);
        if (g.Active && g.EditedSinceStart(this.p.Presets.Presets))
            EUi.WrapColored("開始の後の、品の追加・削除・入れ替え・有効の切り替えは、次の開始から使います（目標数はすぐ反映します）", NoteKind.Warning);
        if (r.Stop is { } s && r.State is FarmState.Stopped && !g.Active)
            EUi.WrapColored($"停止の理由：{s.Message}", NoteKind.Danger);
        if (r.MoveStopProblem is { } mp)
            EUi.WrapColored(mp, NoteKind.Danger);

        if (r.Running && r.Plan?.AdHoc == true)
        {
            using (EUi.HStack())
            {
                EUi.Label($"その場の討伐：{r.Plan.ItemName}・{r.StateName}・倒した数 {r.Kills}");
                // 停止は1つだけ（RSR は元の設定へ戻す）
                if (EUi.SmallButton("停止##adhocStop", ButtonStyle.Danger).Tip("止めて、RSR を開始前の設定へ戻します"))
                    r.StopNow("利用者が止めました", true);
            }
        }

        if (r.State == FarmState.Standby)
            EUi.WrapColored($"条件待ち：{r.Status}", NoteKind.Warning);
        if (r.Retaliating)
            EUi.Label($"反撃中：{r.RetaliationTargetName}");
        if (r.Running && r.LapLimit > 0 && r.LapsWithoutFind > 0)
            EUi.Label($"見つからないまま {r.LapsWithoutFind}/{r.LapLimit} 周");
        if (this.p.RsrControl.ExternalChanges > 0)
            EUi.Muted($"RSR が外から変えられたので戻した回数：{this.p.RsrControl.ExternalChanges}");
        if (this.p.RsrControl.Problem is { } warning)
            EUi.WrapColored(warning, NoteKind.Warning);
        if ((r.Running || g.Active) && this.p.Bmr.Problem is { } bmrWarning)
            EUi.WrapColored(bmrWarning, NoteKind.Warning);
        else if (this.p.Bmr.Combat)
            EUi.Muted(this.p.Bmr.YieldMovement ? "BMR：範囲攻撃を避けています・相手へ寄っています" : "BMR：戦う段の足（範囲攻撃の回避・相手へ寄る）を任せています");
        RsrPanel.Draw(this.p);

        if (EUi.SmallButton("管理画面（環境・記録）"))
            this.openAdmin();
    }

    /// <summary>画面の隅に出る通知（EstellUtils の Toast。マウスを乗せている間は消えない）。</summary>
    private static void Notice(string text, NoteKind kind = NoteKind.Success) => EUi.Toast(text, kind, 6f);

    // ------------------------------------------------------------------
    // モブ（画像1）：モブ名 | ドロップ品 | 座標 | エーテライト | レベル | 拡張 | ラーニング | とらえる可否
    // ------------------------------------------------------------------

    /// <summary>モブの表の行を名前で引く（アイテム・自動討伐のタブだけを開いても作る）。</summary>
    private Dictionary<uint, MobRow> RowsByName(MobCatalog c)
    {
        this.mobRows ??= CatalogQuery.MobRows(c, this.p.Seen.Levels);
        return this.rowsByName ??= this.mobRows.ToDictionary(r => r.NameId);
    }

    private void DrawMobTab(MobCatalog c)
    {
        this.mobRows ??= CatalogQuery.MobRows(c, this.p.Seen.Levels);
        EUi.SearchBox("##mobFilter", ref this.mobFilter, "モブ名・ドロップ品・エリアで探す", width: 320f);
        EUi.Muted("フィールド上に出現するモブのみ記載。右クリックで自動討伐リストに追加出来ます　※青魔・とらえるはオマケ。一部誤っている可能性あり", wrap: true);

        // 並べ替えの見出し（押すと並べ替え・もう一度で逆順。見出しの無い「他 N」の列は押せない）は、行の前に描いて並べ替えを先に反映する
        var listHeight = MathF.Max(RowHeight, EUi.AvailableHeight - RowHeight - EUi.Metrics.ItemSpacing.Y);
        var count = this.mobView?.Count ?? this.mobRows.Count;
        // 見出しの境をつまむと列幅を変えられる（境のダブルクリックでその列だけ戻る。設定へ保存）
        this.mobCols = this.Config.MobColumns.Apply(MobBase).ToArray();
        EUi.SortableTableHeader("##mobsort", this.mobCols, ref this.mobSort, RowHeight, reserveScrollbar: count * RowHeight > listHeight,
            resize: this.Config.MobColumns, onResized: this.Config.Save);

        if (this.mobView == null || this.mobViewFilter != this.mobFilter || this.mobViewSort != this.mobSort)
            this.RebuildMobView();
        var rows = this.mobView!;

        // 見出しは送り領域の外（固定）。行は見えている分だけ描く（309 行でも軽い）
        EUi.VirtualList("##mobRows", rows.Count, RowHeight, listHeight, i => this.DrawMobRow(c, rows[i], i), spacing: 0f);
    }

    private void RebuildMobView()
    {
        IEnumerable<MobRow> q = this.mobRows!;
        var f = this.mobFilter.Trim();
        if (f.Length > 0)
        {
            q = q.Where(r => GameText.Mob(r.NameId).Contains(f, StringComparison.OrdinalIgnoreCase)
                             || GameText.Territory(r.Territory).Contains(f, StringComparison.OrdinalIgnoreCase)
                             || r.Items.Any(i => GameText.Item(i).Contains(f, StringComparison.OrdinalIgnoreCase)));
        }

        Func<MobRow, IComparable> key = this.mobSort.Column switch
        {
            ColName => r => GameText.Mob(r.NameId),
            ColItems => r => r.Items.Count > 0 ? GameText.Item(r.Items[0]) : string.Empty,
            ColPoint or ColMore or ColAetheryte => r => GameText.Territory(r.Territory),
            ColEx => r => r.ExVersion ?? 255,
            ColBlue => r => CatalogDisplay.BlueMagicOrder(this.Catalog?.Mob(r.NameId), r.LearningLevels),
            ColCapture => r => CatalogDisplay.CaptureOrder(this.Catalog?.Mob(r.NameId)),
            _ => r => r.Levels.Count > 0 ? r.Levels[0] : 999, // 不明は最後
        };
        // 青魔法・捕獲の「ー」は逆順でも最後に保つ。
        var missingLast = q.OrderBy(r => this.mobSort.Column switch
        {
            ColBlue => CatalogDisplay.BlueMagicOrder(this.Catalog?.Mob(r.NameId), r.LearningLevels) == int.MaxValue,
            ColCapture => CatalogDisplay.CaptureOrder(this.Catalog?.Mob(r.NameId)) == int.MaxValue,
            _ => false,
        });
        var ordered = this.mobSort.Descending ? missingLast.ThenByDescending(key) : missingLast.ThenBy(key);
        this.mobView = ordered.ThenBy(r => r.NameId).ThenBy(r => r.Territory).ToList();
        this.mobViewFilter = this.mobFilter;
        this.mobViewSort = this.mobSort;
    }

    private void DrawMobRow(MobCatalog c, MobRow row, int index)
    {
        var mob = c.Mob(row.NameId);
        WidgetResult name;
        using (EUi.TableRow(this.mobCols, index, height: RowHeight))
        {
            // モブ名：絵＋名前（右クリックで「自動討伐に追加する」）
            using (EUi.Cell())
            {
                GameIcon.Draw(mob?.Icon ?? 0);
                name = EUi.Selectable($"{GameText.Mob(row.NameId)}##mob{row.NameId}", false)
                    .Tip($"名前 ID {row.NameId}・地点 {row.Points.Count}（要確認 {row.ReviewPoints}）"
                         + (mob?.FateNameMatch == true ? "\nFATE のボスと同名（要確認）" : string.Empty)
                         + "\n候補は未検証です。こちらから仕掛けるのはカタログの範囲内の通常個体だけ"
                         + "\n右クリックで自動討伐に追加");
                if (row.AllReview)
                    EUi.TextColored("要確認", NoteKind.Warning);
            }

            // ドロップ品：絵＋名前を「・」で並べ、入らない分は「他 N」にまとめて全部を吹き出しで出す（EstellUtils 9dca0a4 の InlineList。
            // 絵は drawPrefix で各語の前に描く。入る語の数は絵の幅も含めて決まる）
            var drops = this.DropsInline(row);
            using (EUi.Cell())
                EUi.InlineList(drops.Names, prefixWidth: InlinePrefix, drawPrefix: drops.Draw);

            this.DrawPointCell(row.Points, $"mob{row.NameId}");
            EUi.TableCell(this.AetheryteText(row.Territory, row.Points[0]));
            // レベルが 5 個以上なら「最小〜最大」に縮めて出し、全部は吹き出しで（「4・5・6…」が列で切られて読めなかった）
            EUi.TableCell(CatalogQuery.LevelText(row)).TipIf(row.Levels.Count > 4, string.Join("・", row.Levels));
            EUi.TableCell(row.ExVersion is { } ex ? GameText.ExVersion(ex) : "不明");
            EUi.TableCell(CatalogDisplay.BlueMagicText(mob, row.LearningLevels)).Tip(CatalogDisplay.BlueMagicTooltip(mob, row.LearningLevels));

            // とらえる可否：図鑑の絵＋番号と種類（ドロップ品と同じ並べ方）。名前が全部入っているときは、場所とレベルの説明を吹き出しで出す
            // （切れているときは InlineList が全部の名前を吹き出しで出す。同じフレームに 2 つ出すと重なるので、どちらか 1 つ）
            using (EUi.Cell())
            {
                if (mob == null || mob.Capture.Count == 0)
                {
                    EUi.Label("ー");
                }
                else
                {
                    var captures = this.CapturesInline(mob);
                    var res = EUi.InlineList(captures.Names, prefixWidth: InlinePrefix, drawPrefix: captures.Draw);
                    if (!res.Truncated)
                        res.Tip(CatalogDisplay.CaptureTooltip(mob));
                }
            }
        }

        var menu = this.AddMenuOf(row);
        var pick = EUi.ContextMenu($"mobctx{row.NameId}", name, menu.Entries);
        if (pick >= 0 && pick < menu.Items.Length)
            this.AddItem(menu.Items[pick], [new SourceMobRef { NameId = row.NameId, Territory = row.Territory }]);
    }

    /// <summary>行の右クリックの項目（落とす品ごとに「自動討伐に追加する（品名）」）。行ごとに一度だけ作る。</summary>
    private (MenuEntry[] Entries, uint[] Items) AddMenuOf(MobRow row)
    {
        if (!this.addMenus.TryGetValue(row.NameId, out var menu))
        {
            var items = row.Items.ToArray();
            menu = (items.Select(i => new MenuEntry($"自動討伐に追加する（{GameText.Item(i)}）")).ToArray(), items);
            this.addMenus[row.NameId] = menu;
        }

        return menu;
    }

    private void DrawItemIcon(uint item)
    {
        var icon = this.Catalog?.ItemIcon(item) ?? 0;
        GameIcon.Draw(icon != 0 ? icon : GameText.ItemIcon(item));
    }

    /// <summary>単品の追加（モブ・アイテムの表の右クリック）。追加先は「個別追加」のプリセット1つ（PresetBook.AddItem）。</summary>
    private void AddItem(uint item, List<SourceMobRef> sources)
    {
        var res = this.p.Presets.AddItem(item, sources, this.Config.DefaultTargetCount);
        this.SavePresets();
        Notice($"「{GameText.Item(item)}」を{(res.CreatedPreset ? "新しいプリセット" : "プリセット")}「{res.Preset.Name}」に{(res.CreatedGoal ? "追加" : "まとめて（モブの候補だけ足して）追加")}しました");
    }

    /// <summary>
    /// 座標の欄（2 列を使う）：代表の地点（クリックでその場の討伐）と、地点が複数なら右の列の「他 N」（一覧を開く）。
    /// 「他 N」を専用の列に置くので、座標の文字の長さに関係なく縦にそろう。
    /// </summary>
    private void DrawPointCell(IReadOnlyList<CatalogPoint> points, string id)
    {
        using (EUi.Cell())
            this.DrawPointLink(points[0], $"{id}p0");
        using (EUi.Cell())
        {
            if (points.Count > 1 && EUi.SmallButton($"他 {points.Count - 1}##{id}more"))
                EUi.OpenPopup($"{id}points");
        }

        if (points.Count <= 1)
            return;

        var height = MathF.Min(420f, ((points.Count + 1) * (RowHeight + EUi.Metrics.ItemSpacing.Y)) + 24f);
        using var popup = EUi.Popup($"{id}points", new Vector2(600f, height));
        if (!popup.IsOpen)
            return;
        EUi.Muted("クリックでその場の討伐（このモブだけを倒しに行く）");
        using var scroll = EUi.Scroll($"##{id}pointsScroll", SizeSpec.Fill);
        for (var i = 0; i < points.Count && i < 200; i++)
        {
            if (this.DrawPointLink(points[i], $"{id}pp{i}", withDetail: true))
                EUi.ClosePopup();
        }
    }

    /// <summary>座標の文字（押すとその場の討伐）。押したら true。</summary>
    private bool DrawPointLink(CatalogPoint pt, string id, bool withDetail = false)
    {
        var text = $"{GameText.Territory(pt.Territory)} X:{pt.MapX:0.0} Y:{pt.MapY:0.0}";
        if (withDetail)
        {
            var (levels, observed) = CatalogQuery.PointLevels(pt, this.p.Seen.Levels);
            text += $"　Lv{CatalogQuery.LevelText(new BandEntry(pt.NameId, pt, levels, observed))}・{pt.SourceName}{(pt.NeedsReview ? "・要確認" : string.Empty)}";
        }

        var can = pt.WorldX != null && pt.WorldZ != null;
        var busy = this.p.Goals.Active || this.p.Runner.Running || this.p.Runner.MoveStopPending;
        var tip = busy ? "自動討伐・その場の討伐の実行中は使えません。止めてから押してください" : can
            ? $"{pt.SourceName}（未検証）・元の地点 {pt.Count}{(pt.NeedsReview ? "\n要確認：" + string.Join("・", pt.Reasons) : string.Empty)}\nクリックで、この地点へ移動してこのモブだけを討伐します（移動中は RSR Off・戦うときは攻撃モード〔Henched〕・攻撃してきた敵には反撃・同じ地点を 3 回見回って見つからなければ終える）"
            : "地図の縮尺が分からず、ワールド座標に変換できない地点です（その場の討伐は始められません）";
        // 列の中では残りの幅で頭打ちになり（EstellUtils 35d9ef1）、収まらなければ省略記号。文字は左寄せ。切れたときは全文を吹き出しに足す
        var link = EUi.Button($"{text}##{id}", ButtonStyle.Link, disabled: !can || busy, textAlign: Align.Start);
        var cut = link.Rect.Width + 1f < EUi.ButtonWidth(text);
        if (!link.Tip(cut ? $"{text}\n{tip}" : tip).Clicked)
            return false;
        this.StartAdHoc(pt);
        return true;
    }

    private string AetheryteText(uint territory, CatalogPoint pt)
    {
        if (!this.p.Travel.Available && this.p.World.Player.Territory != territory)
            return "Lifestream なし";
        var key = $"{territory}:{pt.Key}";
        if (this.aetheryteCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(5))
            return hit.Text;
        var near = pt.WorldX is { } x && pt.WorldZ is { } z ? new Vector3(x, 0, z) : (Vector3?)null;
        var id = this.p.Travel.FindAetheryte(territory, near);
        var text = id is { } a ? GameText.Aetheryte(a) : "解放済みなし";
        this.aetheryteCache[key] = (text, DateTime.UtcNow);
        return text;
    }

    /// <summary>クリックした1地点を範囲の基準にし、同じ名前・エリアの基本 ID を使う。</summary>
    private void StartAdHoc(CatalogPoint pt)
    {
        var c = this.p.Catalog;
        if (c == null || pt.WorldX == null || pt.WorldZ == null || this.p.Goals.Active || this.p.Runner.Running || this.p.Runner.MoveStopPending) return;
        var area = HuntAreaBuilder.Build(pt.Territory, pt.NameId, [pt], this.p.Found.For(pt.Territory, pt.NameId));
        if (area == null) return;
        var bases = c.PointsOf(pt.NameId).Where(x => x.Territory == pt.Territory).Select(x => x.BaseId).OfType<uint>().Where(b => b != 0).ToHashSet();
        area = area with { BaseIds = bases };
        var label = $"{GameText.Mob(pt.NameId)}（{GameText.Territory(pt.Territory)} X:{pt.MapX:0.0} Y:{pt.MapY:0.0}）";
        if (this.p.Runner.StartAdHoc(label, area))
            Notice($"その場の討伐を始めました：{label}");
        else
            Notice($"始められません：{this.p.Runner.Stop?.Message}", NoteKind.Warning);
    }

    // ------------------------------------------------------------------
    // 左右に分けた画面（アイテム・自動討伐）
    // ------------------------------------------------------------------

    /// <summary>残りの領域を、左の幅 leftWidth と右の残りに分ける。</summary>
    private static (Rect Left, Rect Right) SplitPanes(float leftWidth)
    {
        var area = EUi.Reserve(SizeSpec.Fill, MathF.Max(RowHeight, EUi.AvailableHeight));
        var left = area.CutLeft(leftWidth, out var rest);
        rest.CutLeft(PaneGap, out var right);
        return (left, right);
    }

    // ------------------------------------------------------------------
    // アイテム（画像2）：左に 10 帯、右に アイテム名 | 座標 | モブ名
    // ------------------------------------------------------------------

    private void DrawItemTab(MobCatalog c)
    {
        var (left, right) = SplitPanes(220f);
        using (EUi.Region(left, EdgeInsets.Zero))
        using (EUi.Card("##bandsCard"))
        {
            for (var i = 0; i < CatalogQuery.Bands.Count; i++)
            {
                var band = CatalogQuery.Bands[i];
                var items = this.BandItems(c, i);
                var res = EUi.Selectable($"{band.Name}（{items.Count} 品）##band{i}", this.bandIndex == i).Tip("右クリックで、この帯の品をまとめて自動討伐に追加");
                if (res.Clicked)
                    this.bandIndex = i;
                if (EUi.ContextMenu($"bandctx{i}", res, $"自動討伐に追加する（{band.PresetName}・{items.Count} 品）") == 0)
                    this.AddBand(band, items);
            }

            EUi.Separator();
            this.unknownLevel ??= CatalogQuery.UnknownLevelEntries(c, this.p.Seen.Levels);
            if (EUi.Selectable($"Lv不明・要確認（{this.unknownLevel.Count} 地点）##bandunk", this.bandIndex == -1))
                this.bandIndex = -1;
        }

        using (EUi.Region(right, EdgeInsets.Zero))
        {
            if (this.bandIndex == -1)
            {
                EUi.Muted("レベルの分からない地点（10 の帯には入れていません）。見つけた対象のレベルは位置と一緒に記録されます", wrap: true);
                this.unknownItems ??= CatalogQuery.UnknownLevelItems(c, GameText.Item, this.p.Seen.Levels);
                this.DrawEntryTable(c, this.unknownItems, "unk");
                return;
            }

            var bandItems = this.BandItems(c, this.bandIndex);
            if (bandItems.Count == 0)
            {
                EUi.Muted($"{CatalogQuery.Bands[this.bandIndex].Name}：この帯に入るレベルの地点がありません（0 品）");
                return;
            }

            EUi.Muted($"{CatalogQuery.Bands[this.bandIndex].Name}：{bandItems.Count} 品（ドロップ元の戦闘 Lv の低い順）。座標は代表の1か所（モブタブと同じ）。品を右クリックで追加、座標をクリックでその場の討伐", wrap: true);
            this.DrawEntryTable(c, bandItems, $"b{this.bandIndex}");
        }
    }

    private List<BandItem> BandItems(MobCatalog c, int index)
    {
        if (!this.bandCache.TryGetValue(index, out var items))
            this.bandCache[index] = items = CatalogQuery.ItemsInBand(c, CatalogQuery.Bands[index], GameText.Item, this.p.Seen.Levels);
        return items;
    }

    /// <summary>
    /// 品ごとに1行：アイテム名 | 代表の座標 | 代表のモブ（座標は代表の 1 つだけ。モブタブの座標と一致させる）。
    /// 代表は CatalogQuery.Representative（モブの表の行そのもの）なので、座標・レベルはモブタブと同じ。
    /// 右クリックの「自動討伐に追加する」は、従来どおりその帯の候補（全部の敵×エリア）を足す（集めるときの別エリアの候補に使う）。
    /// </summary>
    private void DrawEntryTable(MobCatalog c, List<BandItem> items, string id)
    {
        var rows = this.RowsByName(c);
        var listHeight = MathF.Max(RowHeight, EUi.AvailableHeight - RowHeight - EUi.Metrics.ItemSpacing.Y);
        this.itemCols = this.Config.ItemColumns.Apply(ItemBase).ToArray();
        EUi.TableHeader(this.itemCols, reserveScrollbar: items.Count * RowHeight > listHeight, id: "##itemcols", resize: this.Config.ItemColumns, onResized: this.Config.Save);
        EUi.VirtualList($"##items{id}", items.Count, RowHeight, listHeight, i => this.DrawEntryRow(c, rows, items[i], id, i), spacing: 0f);
    }

    private void DrawEntryRow(MobCatalog c, Dictionary<uint, MobRow> rows, BandItem item, string id, int index)
    {
        WidgetResult name;
        var rep = CatalogQuery.Representative(item.Entries.Select(e => (e.NameId, e.Point.Territory)), rows);
        using (EUi.TableRow(this.itemCols, index, height: RowHeight))
        {
            using (EUi.Cell())
            {
                this.DrawItemIcon(item.ItemId);
                name = EUi.Selectable($"{GameText.Item(item.ItemId)}##{id}i{item.ItemId}", false).Tip("右クリックで自動討伐に追加");
            }

            if (rep == null)
            {
                EUi.TableCell("（モブタブに無い敵）", color: EUi.Colors.TextMuted);
            }
            else
            {
                using (EUi.Cell())
                    this.DrawPointLink(rep.Points[0], $"{id}pt{item.ItemId}");
                using (EUi.Cell())
                    this.DrawRepMob(c, rep);
            }
        }

        if (EUi.ContextMenu($"{id}ictx{item.ItemId}", name, $"自動討伐に追加する（{GameText.Item(item.ItemId)}）") == 0)
            this.AddItem(item.ItemId, item.Entries.Select(x => new SourceMobRef { NameId = x.NameId, Territory = x.Point.Territory })
                .GroupBy(s => (s.NameId, s.Territory)).Select(g => g.First()).ToList());
    }

    /// <summary>代表のモブ（絵・名前・モブタブと同じレベルの文・要確認の印）。Cell などの横並びの中で呼ぶ。</summary>
    private void DrawRepMob(MobCatalog c, MobRow rep)
    {
        GameIcon.Draw(c.Mob(rep.NameId)?.Icon ?? 0);
        EUi.Label(GameText.Mob(rep.NameId));
        EUi.Muted($"Lv{CatalogQuery.LevelText(rep)}");
        if (rep.Points[0].NeedsReview)
            EUi.TextColored("要確認", NoteKind.Warning);
    }

    private void AddBand(LevelBand band, List<BandItem> items)
    {
        if (items.Count == 0)
        {
            Notice($"{band.Name} には品がありません", NoteKind.Warning);
            return;
        }

        var preset = this.p.Presets.AddBand(band.PresetName,
            items.Select(it => (it.ItemId, (IEnumerable<SourceMobRef>)it.Entries
                .Select(e => new SourceMobRef { NameId = e.NameId, Territory = e.Point.Territory })
                .GroupBy(s => (s.NameId, s.Territory)).Select(g => g.First()).ToList())),
            this.Config.DefaultTargetCount);
        this.SavePresets();
        Notice($"帯のプリセット「{preset.Name}」を作りました（{preset.Goals.Count} 品）");
    }

    // ------------------------------------------------------------------
    // 自動討伐（画像3）：左にプリセット、右に 有効 | アイテム名 | 所持数 / 目標数 | モブ名 | 座標・地域 | 削除
    // ------------------------------------------------------------------

    private void DrawAutoTab(MobCatalog c)
    {
        var book = this.p.Presets;
        var (left, right) = SplitPanes(240f);
        using (EUi.Region(left, EdgeInsets.Zero))
        {
            // 一覧の上に少し大きめの「開始」。実行中は同じ大きさの赤い「停止」に置き換わる
            this.DrawStartStop(book);

            var buttons = (2f * RowHeight) + (2f * EUi.Metrics.ItemSpacing.Y);
            using (EUi.Scroll("##presetList", SizeSpec.Fill, reserveBelow: buttons))
            {
                var running = this.p.Goals.Active;
                foreach (var pr in book.Presets.ToList())
                {
                    var res = EUi.Selectable($"{(string.IsNullOrEmpty(pr.Name) ? "（名前なし）" : pr.Name)}##pr{pr.Id}", pr.Id == book.SelectedId)
                        .Tip("右クリックで「プリセット名を変更」「削除」");
                    if (res.Clicked)
                    {
                        book.Select(pr.Id);
                        this.SavePresets();
                    }

                    // 右クリック：名前の変更と削除（削除は確認なし）。
                    // 実行中は消せない（計画は開始時の品のまま動くので、消しても集めるのは止まらず、分かりにくいため）
                    switch (EUi.ContextMenu($"prctx{pr.Id}", res,
                                new MenuEntry("プリセット名を変更"),
                                new MenuEntry(running ? "削除（実行中は消せません）" : "削除") { Kind = NoteKind.Danger, Disabled = running }))
                    {
                        case 0:
                            this.renameId = pr.Id;
                            this.renameText = pr.Name;
                            EUi.RequestPopup("renamepreset"); // 小窓は一覧の外（DrawRenamePopup）で描く。要求は ID の文字で照らすので、呼ぶ階層を選ばない
                            break;
                        case 1 when !running:
                            book.Delete(pr.Id);
                            this.SavePresets();
                            Notice($"プリセット「{pr.Name}」を削除しました");
                            break;
                    }
                }
            }

            using (EUi.HStack())
            {
                if (EUi.Button("＋##newPreset").Tip("新しいプリセットを作る"))
                {
                    book.Create("新しいプリセット");
                    this.SavePresets();
                }

                if (EUi.Button("複製##dupPreset", disabled: book.Selected == null).Tip("選んでいるプリセットを複製する") && book.SelectedId is { } sid)
                {
                    book.Duplicate(sid);
                    this.SavePresets();
                }
            }

            if (EUi.Button("Artisan のリストから作る##artisanImport", width: SizeSpec.Fill)
                    .Tip("Artisan の Crafting Lists から、敵が落とす素材だけでプリセットを作ります（目標数＝リストに必要な数）。Artisan の設定は読むだけです"))
            {
                this.artisan = ArtisanLists.Read(ArtisanLists.PathFrom(this.p.ConfigDir));
                EUi.OpenPopup("artisanlists");
            }

            this.DrawArtisanPopup(c);
            this.DrawRenamePopup(book);
        }

        using (EUi.Region(right, EdgeInsets.Zero))
            this.DrawGoals(c, book.Selected);
    }

    /// <summary>
    /// 「開始」：全部のプリセットの有効な品を、上のプリセットから順に集める（GoalRunner.StartAll）。
    /// 実行中（自動討伐・その場の討伐）は同じ大きさの赤い「停止」（RSR は開始前の設定へ戻す）。
    /// </summary>
    private void DrawStartStop(PresetBook book)
    {
        var g = this.p.Goals;
        var r = this.p.Runner;
        WidgetResult res;
        if (g.Active || r.Running)
        {
            // 「開始」と同じ見た目（大きな文字と明るい枠）の赤い版（EstellUtils の ProminentDanger）
            res = EUi.Button("停止##stopAll", ButtonStyle.ProminentDanger, SizeSpec.Fill, height: TopRowHeight);
            if (res.Tip($"{(g.Active ? "自動討伐" : "その場の討伐")}を止めて、RSR を開始前の設定へ戻します"))
            {
                if (g.Active)
                    g.StopAll("利用者が止めました", restore: true);
                else
                    r.StopNow("利用者が止めました", true);
            }

            return;
        }

        var enabled = book.Presets.Sum(x => x.Goals.Count(y => y.Enabled));
        var why = enabled == 0 ? "有効な品がありません（プリセットに品を足して、有効にしてください）"
            : r.MoveStopPending ? "前の移動を止めているところです（止まると押せます）"
            : null;
        // 目立つボタン（EstellUtils の Prominent：大きな文字と明るい枠。押せるボタンだと分かるように）
        res = EUi.Button("開始##startAll", ButtonStyle.Prominent, SizeSpec.Fill, disabled: why != null, height: TopRowHeight);
        if (res.Tip(why ?? $"全部のプリセットの有効な品（{enabled} 品）を、上のプリセットから順に集めます。\n"
                + "見つけた対象だけを RSR の攻撃モード（Henched）で倒し、移動中は RSR を Off にします。攻撃してきた敵には反撃します。\n"
                + "戦っている間は BMR（BossMod Reborn）に範囲攻撃の回避と相手へ寄る動きを任せます（攻撃・ターゲットは任せません）。\n"
                + "目標数に達したら、次の品のモブの所へテレポと飛行で向かいます"))
        {
            var problems = g.StartAll(book.Presets);
            if (problems.Count == 0)
                Notice($"全部のプリセット（{enabled} 品）を上から順に始めました");
            else
                Notice("始められません：" + string.Join(" / ", problems), NoteKind.Warning);
        }
    }

    /// <summary>プリセット名の変更の小窓（右クリックの「プリセット名を変更」）。「変更」か Enter で決める。</summary>
    private void DrawRenamePopup(PresetBook book)
    {
        // 開く要求は右クリックのメニュー（一覧の中）で出し（EUi.RequestPopup）、ここで描く
        var height = (2f * RowHeight) + EUi.Metrics.ItemSpacing.Y + (2f * EUi.Metrics.SpacingSm) + 2f;
        using var popup = EUi.Popup("renamepreset", new Vector2(320f, height), PopupAnchor.MousePosition);
        if (!popup.IsOpen || this.renameId is not { } id)
            return;

        // 開いたらすぐ打てる（autoFocus）。Enter で決める（Submitted。焦点が外れただけの Committed では決めない）
        var enter = EUi.TextInput("##renametext", ref this.renameText, "新しい名前", maxLength: 80, autoFocus: true).Submitted;
        var ok = !string.IsNullOrWhiteSpace(this.renameText);
        using (EUi.HStack())
        {
            if ((EUi.Button("変更##renameOk", ButtonStyle.Primary, disabled: !ok) || enter) && ok)
            {
                book.Rename(id, this.renameText);
                this.SavePresets();
                this.renameId = null;
                EUi.ClosePopup();
            }

            if (EUi.Button("やめる##renameCancel"))
            {
                this.renameId = null;
                EUi.ClosePopup();
            }
        }
    }

    /// <summary>
    /// 右側：見出し（左の「開始」と同じ高さ）→ 一括の行 → 品の行 → 「＋」の行。
    /// 品の削除は行の右クリック（削除の列は無くした：④）。
    /// </summary>
    private void DrawGoals(MobCatalog c, Preset? preset)
    {
        var g = this.p.Goals;

        // 見出しと一括の行は送り領域の外（固定）。つまみが出るとき（品の行＋「＋」の行が入り切らないとき）だけ右端を空けて列をそろえる
        var listHeight = MathF.Max(GoalRowHeight, EUi.AvailableHeight - TopRowHeight - GoalRowHeight - (2f * EUi.Metrics.ItemSpacing.Y));
        var scrollbar = ((preset?.Goals.Count ?? 0) + 1) * GoalRowHeight > listHeight;
        this.goalCols = this.Config.GoalColumns.Apply(GoalBase).ToArray();
        EUi.TableHeader(this.goalCols, TopRowHeight, reserveScrollbar: scrollbar, id: "##goalcols", resize: this.Config.GoalColumns, onResized: this.Config.Save);
        if (preset == null)
        {
            EUi.Muted("左でプリセットを選ぶか、モブ・アイテムのタブで右クリックして「自動討伐に追加する」", wrap: true);
            return;
        }

        this.DrawBulkRow(preset, scrollbar);

        // 進み具合は最後の実行（全部のプリセットをまとめて動かす）の品から、品の ID で引く
        var progress = g.Preset != null ? g.Goals.GroupBy(x => x.Goal.Id).ToDictionary(x => x.Key, x => x.First()) : null;
        var taken = preset.Goals.Select(x => x.ItemId).ToHashSet();
        var rows = this.RowsByName(c);
        Goal? removeGoal = null;
        uint? addItem = null;
        using (EUi.Scroll("##goalRows", SizeSpec.Fill, spacing: 0f))
        {
            for (var i = 0; i < preset.Goals.Count; i++)
            {
                var goal = preset.Goals[i];
                WidgetResult rowResult;

                // hoverable：乗せると行が薄く光り、行のどこでも右クリックを受け取る。左ボタンは掴まないので、中のチェック・品の欄・「−」「＋」は押せる
                // （EstellUtils 27e5a97 の NoCapture。15c7f94 までは行が左ボタンを先に掴み、中の部品が押せなかった）
                using (var row = EUi.TableRow(this.goalCols, i, height: GoalRowHeight, hoverable: true))
                {
                    rowResult = row.Result;
                    using (EUi.Cell())
                    {
                        var en = goal.Enabled;
                        if (EUi.Checkbox($"##en{goal.Id}", ref en, size: BoxSize).Changed)
                        {
                            goal.Enabled = en;
                            preset.Revision++;
                            this.SavePresets();
                        }
                    }

                    // 品の欄：クリックで絞り込みつきの一覧が開き、選び直せる（GBR のプリセットと同じ）
                    using (EUi.Cell())
                    {
                        this.DrawItemIcon(goal.ItemId);
                        if (this.ItemCombo(c, $"##gi{goal.Id}", goal.ItemId, taken, "品を選ぶ…") is { } newItem
                            && PresetBook.ChangeItem(preset, goal, newItem, CatalogQuery.SourcesOf(c, newItem)))
                        {
                            this.SavePresets();
                            Notice($"品を「{GameText.Item(newItem)}」に変えました（目標数はそのまま）");
                        }
                    }

                    using (EUi.Cell())
                    {
                        var count = this.p.World.CountItem(goal.ItemId);
                        CountText(count is { } n ? $"{n} /" : "読めない /");
                        var t = goal.TargetCount;
                        if (EUi.InputInt($"##tc{goal.Id}", ref t, 1, 1, 9999, width: 120f)) // 15c7f94 から行の縦の中央に置かれる
                        {
                            goal.TargetCount = Math.Clamp(t, 1, 9999);
                            preset.Revision++;
                            this.SavePresets();
                        }

                        if (progress != null && progress.TryGetValue(goal.Id, out var pr))
                        {
                            var (text, kind) = pr.State switch
                            {
                                GoalState.Reached => ("達成", NoteKind.Success),
                                GoalState.Running => ("収集中", NoteKind.Info),
                                GoalState.Pending => ("待ち", NoteKind.Info),
                                GoalState.Unavailable => ("収集不可", NoteKind.Warning),
                                _ => ("保留", NoteKind.Warning),
                            };
                            EUi.TextColored(text, kind).Tip(pr.Note);
                        }
                        else if (g.PlanFor(goal, out var why) == null)
                        {
                            EUi.TextColored("要確認", NoteKind.Warning).Tip($"攻撃できる狩り場がありません：{why}");
                        }
                    }

                    // 代表のモブと座標（モブタブと同じ）。候補の行は出さない。
                    // 集めるときは候補（その品を落とす敵×エリア）を全部使う：いまいるエリアに候補があればそこから、無ければ地点の多いエリアから
                    var enabledSources = goal.Sources.Count(s => s.Enabled);
                    var rep = CatalogQuery.Representative(goal.Sources.Where(s => s.Enabled).Select(s => (s.NameId, s.Territory)), rows);
                    using (EUi.Cell())
                    {
                        if (rep != null)
                            this.DrawRepMob(c, rep);
                        else
                            EUi.Muted("（カタログに地点なし）");
                    }

                    if (rep != null && enabledSources > 1)
                        EUi.Tip($"代表のモブ（ほかに候補 {enabledSources - 1} 件。代表で見つからなければ別の候補へ移ります）");

                    using (EUi.Cell())
                    {
                        if (rep != null)
                            this.DrawPointLink(rep.Points[0], $"g{goal.Id}");
                    }
                }

                // 行のどこでも右クリックで「この品を削除」（確認なし）
                if (EUi.ContextMenu($"gctx{goal.Id}", rowResult, new MenuEntry($"この品を削除（{GameText.Item(goal.ItemId)}）") { Kind = NoteKind.Danger }) == 0)
                    removeGoal = goal;
            }

            // 最後の行：品を手で足す（GBR のプリセットの「＋」の行と同じ）。目標数は「一括」の欄の数。
            // この行には縞模様の色を付けない（窓の背景と同じ色）
            using (EUi.TableRow(this.goalCols, preset.Goals.Count, height: GoalRowHeight, striped: false))
            {
                using (EUi.Cell())
                {
                    if (EUi.IconButton(FontAwesomeIcon.Plus.ToIconString(), "##addgoal", ButtonStyle.Normal).Tip($"品を選んで追加（目標数は「一括」の欄の数：{this.bulkTarget}）"))
                        EUi.OpenPopup("addgoalpicker");
                }

                // 小窓は「＋」のすぐ下。品が多くても伸びすぎない大きさ。品が多いので中身は送りの領域（scroll: true）
                using (var picker = EUi.Popup("addgoalpicker", new Vector2(340f, 400f), scroll: true))
                {
                    if (picker.IsOpen)
                    {
                        if (picker.JustOpened)
                            this.itemFilter = string.Empty; // 開くたびに絞り込みを空にする
                        var fromButton = this.DrawItemList(c, 0, taken);
                        if (fromButton != null)
                        {
                            addItem = fromButton;
                            EUi.ClosePopup();
                        }
                    }
                }

                using (EUi.Cell())
                {
                    if (this.ItemCombo(c, "##giadd", 0, taken, "品を選んで追加…") is { } fromCombo)
                        addItem = fromCombo;
                }
            }
        }

        if (addItem is { } add && PresetBook.AddGoal(preset, add, this.bulkTarget, CatalogQuery.SourcesOf(c, add)) is { } added)
        {
            this.SavePresets();
            Notice($"「{GameText.Item(add)}」を追加しました（目標数 {added.TargetCount}）");
        }

        if (removeGoal != null)
        {
            preset.Goals.Remove(removeGoal);
            preset.Revision++;
            this.SavePresets();
            Notice($"「{GameText.Item(removeGoal.ItemId)}」を「{preset.Name}」から削除しました");
        }
    }

    /// <summary>
    /// 見出しのすぐ下の一括の行：有効の列に全部の品の有効をまとめて切り替えるチェック、
    /// 所持数 / 目標数の列に数の欄（初期値 1）と「一括」。欄の数を変えると、選んだプリセットの全部の品の目標数がその数になる。
    /// </summary>
    private void DrawBulkRow(Preset preset, bool scrollbar)
    {
        var rect = EUi.Reserve(SizeSpec.Fill, GoalRowHeight);
        if (scrollbar)
            rect = rect.WithWidth(MathF.Max(0f, rect.Width - EUi.ScrollbarInset())); // 下の送り領域の行と列をそろえる
        using (EUi.Region(rect, EdgeInsets.Zero, 0f))
        using (EUi.TableRow(this.goalCols, 0, height: GoalRowHeight, striped: false))
        {
            using (EUi.Cell())
            {
                // 三つの状態：全部有効＝チェック・一部だけ＝横棒・全部無効＝空。押すと、全部有効なら全部無効へ、それ以外は全部有効へ
                bool? all = preset.Goals.Count == 0 || preset.Goals.All(x => !x.Enabled) ? false
                    : preset.Goals.All(x => x.Enabled) ? true
                    : null;
                if (EUi.Checkbox("##bulkEnabled", ref all, size: BoxSize).Tip(all == true ? "全部の品を無効にする" : "全部の品を有効にする").Changed)
                {
                    PresetBook.SetAllEnabled(preset, all == true);
                    this.SavePresets();
                }
            }

            EUi.SkipCell();
            using (EUi.Cell())
            {
                EUi.Reserve(SizeSpec.Px(CountWidth), EUi.Metrics.WidgetHeight); // 下の行の所持数の文字と同じ幅を空けて、数の欄の位置をそろえる
                if (EUi.InputInt("##bulktarget", ref this.bulkTarget, 1, 1, 9999, width: 120f).Tip("この数にすると、このプリセットの全部の品の目標数がこの数になります（収集中もすぐ反映します。「＋」で足す品の目標数にも使います）").Changed)
                {
                    PresetBook.SetAllTargets(preset, this.bulkTarget);
                    this.SavePresets();
                }

                EUi.Label("一括");
            }
        }
    }

    /// <summary>InlineList の各語の前に空ける幅（絵＋名前との空き）。</summary>
    private static float InlinePrefix => GameIcon.Size + 4f;

    /// <summary>
    /// モブの表の行の「絵＋名前」の並び（名前と、各語の前に絵を描く処理）。行ごとに一度だけ作って使い回す
    /// （drawPrefix にその場でラムダを渡すと、行の数だけ毎フレーム確保が起きるため）。
    /// </summary>
    private readonly Dictionary<uint, (string[] Names, Action<int, Rect> Draw)> dropsInline = [];
    private readonly Dictionary<uint, (string[] Names, Action<int, Rect> Draw)> capturesInline = [];

    private (string[] Names, Action<int, Rect> Draw) DropsInline(MobRow row)
    {
        if (!this.dropsInline.TryGetValue(row.NameId, out var v))
        {
            var icons = row.Items.Select(i => this.Catalog?.ItemIcon(i) is { } icon and not 0 ? icon : GameText.ItemIcon(i)).ToArray();
            v = (row.Items.Select(GameText.Item).ToArray(), (i, r) => GameIcon.DrawAt(icons[i], r));
            this.dropsInline[row.NameId] = v;
        }

        return v;
    }

    private (string[] Names, Action<int, Rect> Draw) CapturesInline(CatalogMob mob)
    {
        if (!this.capturesInline.TryGetValue(mob.NameId, out var v))
        {
            var captures = mob.Capture.OrderBy(b => b.No).DistinctBy(b => b.No).ToList();
            var icons = captures.Select(b => b.Icon).ToArray();
            v = (captures.Select(b => $"{b.No} {b.Species}").ToArray(), (i, r) => GameIcon.DrawAt(icons[i], r));
            this.capturesInline[mob.NameId] = v;
        }

        return v;
    }

    /// <summary>所持数の文字を、決まった幅（<see cref="CountWidth"/>）に右寄せで置く（数の欄の位置を行ごとにそろえる）。</summary>
    private static void CountText(string text)
    {
        var r = EUi.Reserve(SizeSpec.Px(CountWidth), EUi.Metrics.WidgetHeight);
        TextPainter.TextIn(r, EUi.Colors.Text, text, Align.End, Align.Center);
    }

    /// <summary>
    /// 品の選択（GBR のプリセットの品の欄と同じ）：クリックで一覧が開き、上の欄で名前を絞り込める。選んだ品の番号（選ばなければ null）。
    /// 一覧はカタログの品（普段から湧く敵が落とす品）だけ。プリセットにもうある品は「（追加済み）」と出して選べない。
    /// </summary>
    private uint? ItemCombo(MobCatalog c, string id, uint current, IReadOnlySet<uint> taken, string placeholder)
    {
        using var list = EUi.ComboBody(id, current == 0 ? placeholder : GameText.Item(current), listHeight: 320f);
        if (!list.IsOpen)
            return null;
        if (list.JustOpened)
            this.itemFilter = string.Empty; // 開くたびに絞り込みを空にする
        var picked = this.DrawItemList(c, current, taken);
        if (picked != null)
            list.Close();
        return picked;
    }

    /// <summary>品の選択の中身（絞り込みの欄と一覧）。品の欄の一覧と「＋」のボタンの小窓で同じものを使う。選んだ品の番号（選ばなければ null）。</summary>
    private uint? DrawItemList(MobCatalog c, uint current, IReadOnlySet<uint> taken)
    {
        this.comboItems ??= c.Items.Select(i => (i.Id, GameText.Item(i.Id))).ToList();
        EUi.SearchBox("##itemfilter", ref this.itemFilter, "絞り込み…");
        var list = CatalogQuery.FilterItems(this.comboItems, this.itemFilter);
        if (list.Count == 0)
            EUi.Muted("見つかりません（この一覧は敵が落とす素材だけです）", wrap: true);

        uint? picked = null;
        foreach (var (item, name) in list)
        {
            // 見えていない行は高さだけ取る（品が多くても軽く）
            if (!EUi.IsRowVisible(RowHeight))
            {
                EUi.Reserve(SizeSpec.Fill, RowHeight);
                continue;
            }

            var used = taken.Contains(item) && item != current;
            var row = EUi.SelectableRow($"##ci{item}", item == current, height: RowHeight, disabled: used);
            using (row)
            {
                this.DrawItemIcon(item);
                EUi.Label(used ? $"{name}（追加済み）" : name, used ? EUi.Colors.TextDisabled : null);
            }

            if (row.Clicked && !used)
                picked = item;
        }

        return picked;
    }

    /// <summary>Artisan のリストを選ぶ小窓。選んだリストの、敵が落とす素材だけで新しいプリセットを作る。</summary>
    private void DrawArtisanPopup(MobCatalog c)
    {
        using var popup = EUi.Popup("artisanlists", new Vector2(380f, 320f));
        if (!popup.IsOpen)
            return;
        if (this.artisan.Error != null)
        {
            EUi.WrapColored(this.artisan.Error, NoteKind.Warning);
            return;
        }

        if (this.artisan.Lists.Count == 0)
        {
            EUi.Muted("Artisan にリストがありません");
            return;
        }

        EUi.Muted("リストを選ぶと、敵が落とす素材だけで新しいプリセットを作ります", wrap: true);
        using var scroll = EUi.Scroll("##artisanScroll", SizeSpec.Fill);
        foreach (var list in this.artisan.Lists)
        {
            if (EUi.Selectable($"{(list.Name.Length > 0 ? list.Name : "（名前なし）")}（レシピ {list.Recipes.Count}）##al{list.Id}", false))
            {
                this.ImportArtisan(c, list);
                EUi.ClosePopup();
            }
        }
    }

    private void ImportArtisan(MobCatalog c, ArtisanList list)
    {
        var unknown = new List<uint>();
        var materials = ArtisanLists.Materials(list, GameText.RecipeIngredients, unknown);
        var split = ArtisanLists.Split(materials, i => c.MobsDropping(i).Count > 0, unknown);
        var others = $"ほかの入手方法の素材 {split.Others.Count} 品は入れていません"
                     + (split.UnknownRecipes.Count > 0 ? $"・ゲームの表に無いレシピ {split.UnknownRecipes.Count} 件" : string.Empty);
        if (split.MobDrops.Count == 0)
        {
            Notice($"「{list.Name}」に敵が落とす素材はありません（{others}）", NoteKind.Warning);
            return;
        }

        var preset = this.p.Presets.AddImported($"Artisan：{list.Name}",
            split.MobDrops.Select(d => (d.ItemId, d.Count, (IEnumerable<SourceMobRef>)ArtisanLists.SourcesOf(c, d.ItemId))));
        this.SavePresets();
        Notice($"「{preset.Name}」を作りました：敵が落とす素材 {preset.Goals.Count} 品（{others}）");
    }

    private void SavePresets()
    {
        this.Config.SelectedPresetId = this.p.Presets.SelectedId;
        this.Config.SinglePresetId = this.p.Presets.SingleId;
        this.Config.Save();
    }

    // ------------------------------------------------------------------
    // 必須プラグイン
    // ------------------------------------------------------------------

    /// <summary>導入済みの一覧から作った状態（1秒に1回だけ読み直す。読み直したら、有効にしている途中のプラグインが読み込まれたかも見る）。</summary>
    private List<RequiredPlugins.State> PluginStates()
    {
        if (DateTime.UtcNow - this.pluginStatesAt <= TimeSpan.FromSeconds(1))
            return this.pluginStates;

        this.pluginStatesAt = DateTime.UtcNow;
        try
        {
            this.pluginStates = RequiredPlugins.Check(RequiredPlugins.Read(Svc.PluginInterface.InstalledPlugins));
        }
        catch (Exception)
        {
            // 一覧を読めない：前の状態のまま
        }

        this.pluginRepos = PluginInstaller.ReadRepos();
        this.installer.Observe(this.pluginStates);
        return this.pluginStates;
    }

    private void DrawRequiredTab()
    {
        var states = this.PluginStates();
        EUi.Paragraph("AutoMobFarm が使うプラグインです。「必須」が無いと収集を始められません。"
                      + "足りないものは「インストール」を押すと、配布元の登録からインストールまで行います（使えるものにはボタンを出しません）。");
        var unavailable = RequiredPlugins.Unavailable(states);
        EUi.WrapColored(unavailable == 0 ? "すべて使えます" : $"使えないプラグイン：{unavailable} 件", unavailable == 0 ? NoteKind.Success : NoteKind.Warning);
        EUi.Separator();

        using var scroll = EUi.Scroll("##reqScroll", SizeSpec.Fill);
        foreach (var state in states)
            this.DrawRequiredCard(state);
    }

    /// <summary>1 つのプラグインのカード：名前・必須／推奨・状態、作者、使う場面、足りなければボタン。</summary>
    private void DrawRequiredCard(RequiredPlugins.State state)
    {
        var e = state.Entry;
        var job = this.installer.JobOf(e.InternalName);
        var button = RequiredPlugins.ButtonFor(state.Status);
        using var card = EUi.Card($"##req_{e.InternalName}");

        using (EUi.HStack())
        {
            EUi.TextColored(e.DisplayName, new Vector4(0.7f, 0.9f, 1f, 1f));
            EUi.Badge(e.Need == RequiredPlugins.Need.Required ? "必須" : "推奨", e.Need == RequiredPlugins.Need.Required ? NoteKind.Danger : NoteKind.Info);
            EUi.TextColored(RequiredPlugins.StatusText(state), state.Status switch
            {
                RequiredPlugins.Status.Loaded => NoteKind.Success,
                RequiredPlugins.Status.NotLoaded => NoteKind.Warning,
                _ => NoteKind.Danger,
            });
        }

        // 他所の成果を紹介しているので、作者名は説明より前に出す
        EUi.Muted($"開発者：{e.Authors} 様", wrap: true);
        EUi.Muted($"使う場面：{e.UsedFor}", wrap: true);
        if (state.Status != RequiredPlugins.Status.Loaded)
            EUi.Muted($"無いと：{e.IfMissing}", wrap: true);
        if (e.Note != null)
            EUi.WrapColored(e.Note, NoteKind.Warning);

        // 使えるものにはボタンを出さない
        if (button != RequiredPlugins.Button.None)
        {
            if (button == RequiredPlugins.Button.Install)
                EUi.Muted($"配布元：{e.RepoUrls[0]}（{RepoText(RequiredPlugins.RepoState(e, this.pluginRepos))}）", wrap: true);

            var busy = job is { Busy: true };
            using (EUi.HStack())
            {
                if (button == RequiredPlugins.Button.Install)
                {
                    if (EUi.Button(busy ? $"インストール中…##reqi{e.InternalName}" : $"インストール##reqi{e.InternalName}", ButtonStyle.Primary, SizeSpec.Px(160f), disabled: busy)
                            .Tip(busy ? job!.Message : "配布元の登録からインストールまで行います"))
                        this.installer.Install(e);
                }
                else if (EUi.Button(busy ? $"有効にしています…##reqe{e.InternalName}" : $"有効にする##reqe{e.InternalName}", ButtonStyle.Primary, SizeSpec.Px(160f), disabled: busy)
                             .Tip(busy ? job!.Message : "Dalamud の /xlenableplugin で有効にします（プラグイン一覧の切り替えと同じ）"))
                {
                    this.installer.Enable(e);
                }

                // 自動でうまくいかなかったときだけ、手で入れる入口を出す
                if (job is { Busy: false, Ok: false })
                {
                    if (EUi.Button($"{(state.Status == RequiredPlugins.Status.Missing ? "インストール画面" : "プラグイン一覧")}を開く##reqo{e.InternalName}", width: SizeSpec.Px(190f)))
                        PluginInstaller.OpenInstaller(e, state.Status != RequiredPlugins.Status.Missing);
                    if (button == RequiredPlugins.Button.Install
                        && EUi.Button($"配布元の URL をコピー##reqc{e.InternalName}", width: SizeSpec.Px(190f)).Tip("Dalamud の設定の「カスタムプラグインリポジトリ」に貼り付けます"))
                    {
                        EUi.SetClipboard(e.RepoUrls[0]);
                        Notice($"{e.DisplayName} の配布元の URL をコピーしました");
                    }
                }
            }
        }

        // 結果の文。使えるようになった後は、成功の文だけ残す（手で入れた後に古い失敗の文が残らないように）
        if (job != null && !string.IsNullOrEmpty(job.Message) && (button != RequiredPlugins.Button.None || job.Ok == true))
        {
            if (job.Ok == null)
                EUi.Muted(job.Message, wrap: true);
            else
                EUi.WrapColored(job.Message, job.Ok == true ? NoteKind.Success : NoteKind.Warning);
        }
    }

    private static string RepoText(RequiredPlugins.RepoStatus status) => status switch
    {
        RequiredPlugins.RepoStatus.Added => "登録済み",
        RequiredPlugins.RepoStatus.Disabled => "登録済み・無効：押すと有効に戻します",
        RequiredPlugins.RepoStatus.Missing => "未登録：押すと登録します",
        _ => "登録されているか読めません",
    };
}
