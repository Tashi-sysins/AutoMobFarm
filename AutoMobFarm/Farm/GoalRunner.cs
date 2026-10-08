using System;
using System.Collections.Generic;
using System.Linq;
using AutoMobFarm.Judge;
using AutoMobFarm.Data;

namespace AutoMobFarm.Farm;

/// <summary>1品の目標の進み具合。</summary>
public enum GoalState
{
    /// <summary>まだ（これから集める）。</summary>
    Pending,

    /// <summary>いま集めている。</summary>
    Running,

    /// <summary>所持数が目標に達した。</summary>
    Reached,

    /// <summary>収集できない（カタログに地点が無い・品の対応が無い）。</summary>
    Unavailable,

    /// <summary>集めようとしたが、収集を続けられない理由で止まった（見つからない・進展なし・移動できない）。ほかの品へ進む。</summary>
    Held,
}

public sealed class GoalProgress
{
    public required Goal Goal { get; init; }

    public HashSet<uint> TriedTerritories { get; } = [];

    public GoalState State { get; set; }

    public string Note { get; set; } = string.Empty;

    /// <summary>この品の計画（開始時に決めた、カタログの狩り場の集合）。収集できない品は null。</summary>
    public FarmPlan? Plan { get; set; }
}

/// <summary>
/// 複数品の収集。品ごとに、単品・1エリアの FarmRunner をそのまま使う（戦闘の制御は増やさない）。
///  ・品ごとの計画は「その品を落とす候補（プリセットの SourceMobRef）と一致する、カタログの地点」だけを許す（R1：候補一覧の全体を渡さない）。
///    計画は開始時に決め、途中で地点が増えても、その実行では使わない。
///  ・既に目標以上の品は集めない。カタログに地点が無い品は「収集不可」。収集を続けられない理由で止まった品は「保留」にして次の品へ進む。
///  ・利用者の停止・ログアウト・読み込みの解除・例外では全体を止める。読めない間は待ち、満杯なら次の品へ進む。
///  ・品と品のあいだは RSR を Off のまま持ち続け（FarmRunner.KeepRsrOnFinish）、最後にまとめて元の設定へ戻す（全部終えた・利用者の「停止（元に戻す）」）。
///  ・全部の品が目標に達したときだけ「全収集完了」。未達の品が残れば「未達あり」。
/// </summary>
public sealed class GoalRunner
{
    /// <summary>これで止まったら、ほかの品へ進まず全体を止める（利用者の停止・ログアウト・読み込みの解除・例外）。</summary>
    public static readonly IReadOnlySet<string> StopAllCodes = new HashSet<string> { "user", "user-hold", "logout", "unload", "exception" };
    public static readonly IReadOnlySet<string> NextAreaCodes = new HashSet<string> { "nofind", "move", "mesh", "travel", "height" };

    private readonly FarmRunner runner;
    private readonly IFarmWorld world;
    private readonly FoundSpotStore found;
    private readonly Func<uint, uint, IReadOnlyList<CatalogPoint>> pointsOf;
    private readonly Func<uint, IReadOnlyCollection<uint>?> dropsOf;
    private readonly Func<uint, string> itemName;
    private readonly List<GoalProgress> goals = [];
    private GoalProgress? current;

    // 並びの順に集めるか（画面の「開始」＝全部のプリセットを上から順に）。false なら、いまいるエリアの品を先に（プリセット単独の開始）
    private bool inOrder;

    // 開始したときの各プリセットの品の顔ぶれ（目標数を除く。実行中に品の追加・削除・有効の切り替えがあったかを画面に出すため）
    private readonly Dictionary<string, string> startShapes = [];

    // 達成済みの品の所持数を読み直した時刻（目標数を上げたら集め直す。毎フレーム全部の品の所持数は読まない）
    private DateTime lastReachedCheck = DateTime.MinValue;

    /// <summary>達成済みの品の所持数を読み直す間隔。</summary>
    public static readonly TimeSpan ReachedCheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>全部のプリセットをまとめて動かすときの、まとめのプリセットの ID。</summary>
    public const string AllPresetsId = "all-presets";

    /// <param name="dropsOf">品を落とすモブの名前 ID（カタログ）。分からなければ null（プリセットの候補をそのまま使う）。</param>
    public GoalRunner(FarmRunner runner, IFarmWorld world, Func<uint, uint, IReadOnlyList<CatalogPoint>> pointsOf, FoundSpotStore found, Func<uint, IReadOnlyCollection<uint>?> dropsOf, Func<uint, string> itemName)
    {
        this.runner = runner;
        this.world = world;
        this.found = found;
        this.pointsOf = pointsOf;
        this.dropsOf = dropsOf;
        this.itemName = itemName;
    }

    /// <summary>実行中のプリセット（実行していなければ最後に実行したもの）。</summary>
    public Preset? Preset { get; private set; }

    /// <summary>開始時のプリセットの版（実行中に編集されても、計画は開始時のまま）。</summary>
    public int PresetRevision { get; private set; }

    /// <summary>複数品の収集を管理しているか（条件待ちを含む）。</summary>
    public bool Active { get; private set; }

    public string Status { get; private set; } = "待機";

    public IReadOnlyList<GoalProgress> Goals => this.goals;

    public GoalProgress? Current => this.current;

    /// <summary>
    /// 品ごとの計画を作る（カタログの地点のうち、その品の候補のモブ・エリアに一致するものだけ）。
    /// エリアが複数あれば、いまいるエリアを優先し、無ければカタログの地点が多いエリアを選ぶ。作れなければ null と理由。
    /// </summary>
    public FarmPlan? PlanFor(Goal goal, out string why, IReadOnlySet<uint>? exclude = null)
    {
        var me = this.world.Player;

        var drop = this.dropsOf(goal.ItemId);
        if (!goal.Sources.Any(s => s.Enabled))
        {
            why = "落とすモブの候補が有効になっていません";
            return null;
        }

        var sources = goal.Sources.Where(s => s.Enabled && (drop == null || drop.Contains(s.NameId))).ToList();

        var areas = new List<HuntArea>();
        foreach (var g in sources.Where(s => exclude == null || !exclude.Contains(s.Territory)).GroupBy(s => (s.Territory, s.NameId)))
        {
            var all = this.pointsOf(g.Key.NameId, g.Key.Territory);
            // 候補に地点の指定（PointKey）があればその地点だけ。null が1つでもあればエリア全体
            var pts = g.Any(s => s.PointKey == null) ? all : all.Where(p => g.Any(s => s.PointKey == p.Key)).ToList();
            if (HuntAreaBuilder.Build(g.Key.Territory, g.Key.NameId, pts, this.found.For(g.Key.Territory, g.Key.NameId)) is { } a)
                areas.Add(a with { BaseIds = all.Select(p => p.BaseId).OfType<uint>().Where(b => b != 0).ToHashSet() });
        }

        if (areas.Count == 0)
        {
            why = exclude is { Count: > 0 }
                ? $"ほかのエリアにカタログの地点がありません（試したエリア {string.Join("・", exclude)}）"
                : $"カタログに地点がありません（モブの候補 {sources.Count} 件）";
            return null;
        }

        // いまいるエリアを優先。無ければカタログの地点（Anchor）の多いエリア
        var territory = areas.Any(a => a.Territory == me.Territory)
            ? me.Territory
            : areas.GroupBy(a => a.Territory).OrderByDescending(x => x.Sum(a => a.Points.Count(p => p.Anchor))).ThenBy(x => x.Key).First().Key;
        why = string.Empty;
        return new FarmPlan(goal.ItemId, this.itemName(goal.ItemId), goal.TargetCount, territory, areas.Where(a => a.Territory == territory).ToList());
    }

    /// <summary>
    /// 全部のプリセットの有効な品を、上から順に（プリセットの並び → 品の並び）集める（画面の「開始」）。
    /// 品の集め方・反撃・RSR の扱いは1つのプリセットのときと同じ。始められない理由（空なら始めた）。
    /// </summary>
    public List<string> StartAll(IReadOnlyList<Preset> presets)
    {
        var all = new Preset { Id = AllPresetsId, Name = "全部のプリセット", Goals = presets.SelectMany(p => p.Goals).ToList() };
        return this.Begin(all, inOrder: true, presets);
    }

    /// <summary>1つのプリセットを始める（いまいるエリアの品を先に）。始められない理由（空なら始めた）。</summary>
    public List<string> Start(Preset preset) => this.Begin(preset, inOrder: false, [preset]);

    /// <summary>
    /// 開始の後に、品の追加・削除・品の入れ替え・有効の切り替え・モブの候補の変更があったプリセットがあるか（これらは次の開始から使う）。
    /// 目標数の変更は数えない（すぐ反映する：<see cref="SyncTargets"/>）。
    /// </summary>
    public bool EditedSinceStart(IEnumerable<Preset> presets)
        => presets.Any(p => this.startShapes.TryGetValue(p.Id, out var shape) && shape != Shape(p));

    /// <summary>プリセットの品の顔ぶれ（目標数を除く）。</summary>
    private static string Shape(Preset p)
        => string.Join("|", p.Goals.Select(g => $"{g.Id}:{g.ItemId}:{g.Enabled}:{string.Join(",", g.Sources.Select(s => $"{s.NameId}/{s.Territory}/{s.PointKey}/{s.Enabled}"))}"));

    private List<string> Begin(Preset preset, bool inOrder, IEnumerable<Preset> sources)
    {
        var problems = new List<string>();
        if (this.Active || this.runner.Running)
            problems.Add("ほかの収集が動いています（止めてから始めてください）");
        var enabled = preset.Goals.Where(g => g.Enabled).ToList();
        if (enabled.Count == 0)
            problems.Add("有効な品がありません");
        if (problems.Count > 0)
            return problems;

        this.startShapes.Clear();
        foreach (var s in sources)
            this.startShapes[s.Id] = Shape(s);
        this.Preset = preset;
        this.PresetRevision = preset.Revision;
        this.inOrder = inOrder;
        this.goals.Clear();
        foreach (var g in enabled)
        {
            var progress = new GoalProgress { Goal = g };
            var count = this.world.CountItem(g.ItemId);
            if (count is { } c && c >= g.TargetCount)
            {
                progress.State = GoalState.Reached;
                progress.Note = $"既に {c} 個（目標 {g.TargetCount}）";
            }
            else if (this.PlanFor(g, out var why) is { } plan)
            {
                progress.State = GoalState.Pending;
                progress.Plan = plan;
            }
            else
            {
                progress.State = GoalState.Unavailable;
                progress.Note = "収集不可：" + why;
            }

            this.goals.Add(progress);
        }

        this.Active = true;
        Core.DebugLog.Current?.Line("自動討伐", $"開始：「{preset.Name}」（版 {preset.Revision}）・品 {this.goals.Count}（集める {this.goals.Count(x => x.State == GoalState.Pending)}・既に達成 {this.goals.Count(x => x.State == GoalState.Reached)}・収集不可 {this.goals.Count(x => x.State == GoalState.Unavailable)}）");
        this.StartNext();
        return [];
    }

    /// <summary>次の品を始める（並びの順。プリセット単独の開始では、いまいるエリアの品を先に）。無ければ終える。</summary>
    private void StartNext(GoalProgress? preferred = null)
    {
        while (true)
        {
            var pending = this.goals.Where(g => g.State == GoalState.Pending);
            var next = preferred ?? (this.inOrder
                ? pending.FirstOrDefault()
                : pending.OrderByDescending(g => g.Plan!.Territory == this.world.Player.Territory).FirstOrDefault());
            preferred = null; // 同じ品の別エリアを一度優先。開始不可なら次の品へ進む。
            if (next == null)
            {
                this.FinishAll();
                return;
            }

            this.current = next;
            next.State = GoalState.Running;
            this.runner.KeepRsrOnFinish = true;

            // 目標数は始める時点の値を使う（開始の後に画面で変えた数もすぐ効かせる）
            next.Plan = next.Plan! with { TargetCount = next.Goal.TargetCount };
            if (this.runner.Start(next.Plan))
            {
                this.Status = $"「{this.itemName(next.Goal.ItemId)}」を集めています";
                return;
            }

            // 始められない品は保留にして次の品へ（全体は止めない：D10・S3）
            next.State = GoalState.Held;
            next.Note = "始められません：" + (this.runner.Stop?.Message ?? "理由不明");
            Core.DebugLog.Current?.Line("自動討伐", next.Note);
            this.current = null;
        }
    }

    /// <summary>毎フレーム（FarmRunner.Tick の後）。単品の収集の結果を見て、次の品へ進む・全体を止める。</summary>
    public void Tick()
    {
        if (!this.Active)
            return;
        this.SyncTargets();
        if (this.current is not { } cur)
            return;

        switch (this.runner.State)
        {
            case FarmState.Done:
                cur.State = GoalState.Reached;
                cur.Note = this.runner.Stop?.Message ?? "目標に達しました";
                this.current = null;
                this.StartNext();
                break;
            case FarmState.Stopped when this.runner.Stop is { } areaStop && NextAreaCodes.Contains(areaStop.Code):
                cur.TriedTerritories.Add(cur.Plan!.Territory);
                if (this.PlanFor(cur.Goal, out var why, cur.TriedTerritories) is { } next)
                {
                    cur.Plan = next;
                    cur.State = GoalState.Pending;
                    cur.Note = $"{areaStop.Message}（別のエリア {next.Territory} を試します）";
                }
                else
                {
                    cur.State = GoalState.Held;
                    cur.Note = $"保留：{areaStop.Message}（ほかのエリアにも行けません：{why}）";
                }

                Core.DebugLog.Current?.Line("自動討伐", cur.Note);
                this.current = null;
                this.StartNext(cur.State == GoalState.Pending ? cur : null);
                break;
            case FarmState.Stopped when this.runner.Stop is { } stop && !StopAllCodes.Contains(stop.Code):
                cur.State = GoalState.Held;
                cur.Note = $"保留：{stop.Message}";
                Core.DebugLog.Current?.Line("自動討伐", $"「{this.itemName(cur.Goal.ItemId)}」を保留にして次の品へ：{stop.Message}");
                this.current = null;
                this.StartNext();
                break;
            case FarmState.Stopped:
                cur.State = GoalState.Held;
                cur.Note = this.runner.Stop?.Message ?? "止まりました";
                this.current = null;
                this.Active = false;
                this.runner.KeepRsrOnFinish = false;

                // 保持が必要な理由では FarmRunner が保持済み。利用者の通常停止で制御が残っていれば元へ戻す
                this.runner.ReleaseRsr(restore: true, cur.Note);
                this.Status = $"止まりました：{cur.Note}";
                Core.DebugLog.Current?.Line("自動討伐", this.Status);
                break;
            default:
                this.Status = $"「{this.itemName(cur.Goal.ItemId)}」：{this.runner.StateName}（{this.goals.Count(g => g.State == GoalState.Reached)}/{this.goals.Count} 品が達成）";
                break;
        }
    }

    /// <summary>
    /// 画面で変えた目標数をすぐ反映する。
    ///  ・集めている品：FarmRunner の目標を変える（下げて届いていればその場で終え、上げれば集め続ける）。
    ///  ・達成済みの品：目標数が所持数より上になったら「待ち」に戻す（並びの順に、また集める）。1 秒ごとに読み直す。
    ///  ・これから集める品：始めるときに今の目標数を使う（StartNext）。
    /// 全部を終えた後（Active でない）は何もしない（次の開始から）。
    /// </summary>
    private void SyncTargets()
    {
        if (this.current is { State: GoalState.Running } running)
            this.runner.SetTarget(running.Goal.TargetCount);

        var now = this.world.Now;
        if (now - this.lastReachedCheck < ReachedCheckInterval)
            return;
        this.lastReachedCheck = now;
        foreach (var g in this.goals.Where(g => g.State == GoalState.Reached))
        {
            if (this.world.CountItem(g.Goal.ItemId) is not { } c || c >= g.Goal.TargetCount)
                continue;

            // 開始の時点で既に達成していた品は計画が無いので、ここで作る
            var why = string.Empty;
            g.Plan ??= this.PlanFor(g.Goal, out why);
            if (g.Plan == null)
            {
                g.State = GoalState.Unavailable;
                g.Note = $"目標数を {g.Goal.TargetCount} に上げましたが、収集不可：{why}";
            }
            else
            {
                g.State = GoalState.Pending;
                g.Note = $"目標数を {g.Goal.TargetCount} に上げたので、また集めます（いま {c} 個）";
            }

            Core.DebugLog.Current?.Line("自動討伐", $"「{this.itemName(g.Goal.ItemId)}」：{g.Note}");
        }
    }

    private void FinishAll()
    {
        this.Active = false;
        this.current = null;
        this.runner.KeepRsrOnFinish = false;
        this.runner.ReleaseRsr(restore: true, "複数品の収集を終えた");
        var left = this.goals.Where(g => g.State != GoalState.Reached).ToList();
        this.Status = left.Count == 0
            ? $"全収集完了（{this.goals.Count} 品）"
            : $"未達あり：{string.Join("・", left.Select(g => $"{this.itemName(g.Goal.ItemId)}（{g.Note}）"))}";
        Core.DebugLog.Current?.Line("自動討伐", this.Status);
    }

    /// <summary>止める（利用者の操作）。<paramref name="restore"/> なら RSR を元の設定へ戻し、そうでなければ Off のまま保持する。</summary>
    public void StopAll(string reason, bool restore)
    {
        if (this.runner.Running)
            this.runner.StopNow(reason, restore);
        this.runner.KeepRsrOnFinish = false;
        this.runner.ReleaseRsr(restore, reason);
        if (this.current is { } cur && cur.State == GoalState.Running)
        {
            cur.State = GoalState.Held;
            cur.Note = reason;
        }

        this.current = null;
        this.Active = false;
        this.Status = $"止めました：{reason}";
        Core.DebugLog.Current?.Line("自動討伐", this.Status);
    }
}
