using System;
using System.Numerics;

namespace AutoMobFarm.Farm;

/// <summary>
/// 指定地点の近くまで移動する（vnavmesh の取り消せる経路だけを使う）。
///
///  ・遠ければ（<see cref="MountOver"/> 超）マウントに乗り、このエリアで飛べるなら飛ぶ。乗れない・飛べない場所では歩く。
///  ・乗り終わるまで歩き出さない。マウントが使えるようになるのを待ってから頼み、1.5秒で乗り始めなければ頼み直す（4回まで。テレポ直後の取りこぼし）。
///  ・戦闘中の状態が残っていて乗れないときは 8秒まで待つ（倒した直後）。
///  ・4秒動かない・8秒近づかない・経路探しが10秒終わらないと「動けない」として引き直す。歩いていたなら乗って飛ぶ。6回で失敗。
///  ・歩きの経路が途中で途切れている（最後の区間が 3m 超：壁・崖の向こう）なら、乗って飛ぶ経路に切り替える。
///  ・行き先が上空（巡回の点の上）でも、歩くときは地面の点（<c>walkTo</c>）へ地上の経路を頼む。vnavmesh の地上の経路は
///    目的地の周り（上下 ±5m）に歩ける面が要り、空中の点へは引けない（エラーを出して返し、乗って飛ぶことになる）。
///  ・作業時間の上限（既定 3分）を過ぎたら失敗。
/// 止めるときは自分の経路だけを止める（OwnPath）。遅れて返った探索の結果では歩き出さない。
/// </summary>
public sealed class Mover
{
    public enum Result
    {
        Running,
        Arrived,
        Failed,
    }

    /// <summary>行き先までこれより遠ければマウントに乗る（m）。</summary>
    public const float MountOver = 30f;

    public static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan NotCloserLimit = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan SearchLimit = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CombatClearWait = TimeSpan.FromSeconds(8);
    public const int StuckLimit = 6;
    public const int MountAttemptLimit = 4;
    public const int NoPathLimit = 3;

    private readonly OwnPath path = new() { DropCutOffEnd = true };

    private Vector3 destination;
    private Vector3? walkTo;
    private float range;
    private string label = string.Empty;
    private TimeSpan limit;
    private DateTime startedAt;

    private bool requested;
    private bool fly;
    private int mountAttempts;
    private DateTime? mountIssuedAt;
    private DateTime? mountWaitSince;
    private DateTime? combatWaitSince;
    private bool forceMount;

    private Vector3 progressAt;
    private DateTime progressSince;
    private float closest = float.MaxValue;
    private DateTime closestAt;
    private DateTime? searchSince;
    private int stuckCount;
    private int noPath;
    private int? waypointsLeft;

    /// <summary>移動中か（探索中・自分の経路の追従中）。</summary>
    public bool Active { get; private set; }

    /// <summary>行き先。</summary>
    public Vector3 Destination => this.destination;

    /// <summary>いまの状態の説明（画面・記録用）。</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>失敗の理由（Failed のとき）。</summary>
    public string Problem { get; private set; } = string.Empty;

    /// <summary>飛んで行く経路を頼んだか（試験・記録用）。</summary>
    public bool Flying => this.fly;

    /// <summary>移動を始める（前の移動の経路は止めずに手放す。新しい経路が届くまで歩き続ける）。</summary>
    /// <param name="walkTo">歩くときの行き先（<paramref name="to"/> が上空のときの地面の点）。null なら歩くときも <paramref name="to"/>。</param>
    public void Start(ICombatWorld world, Vector3 to, float arriveRange, string what, TimeSpan? workLimit = null, Vector3? walkTo = null)
    {
        this.path.Release();
        this.destination = to;
        this.walkTo = walkTo;
        this.range = arriveRange;
        this.label = what;
        this.limit = workLimit ?? TimeSpan.FromMinutes(3);
        this.startedAt = world.Now;
        this.requested = false;
        this.fly = false;
        this.mountAttempts = 0;
        this.mountIssuedAt = null;
        this.mountWaitSince = null;
        this.combatWaitSince = null;
        this.forceMount = false;
        this.progressAt = world.MyPosition;
        this.progressSince = world.Now;
        this.closest = float.MaxValue;
        this.closestAt = world.Now;
        this.searchSince = null;
        this.stuckCount = 0;
        this.noPath = 0;
        this.waypointsLeft = null;
        this.Active = true;
        this.Problem = string.Empty;
        this.Status = $"{what} へ移動を始めます";
    }

    /// <summary>水平の残り距離。</summary>
    public float FlatDistance(Vector3 me) => Vector2.Distance(new Vector2(me.X, me.Z), new Vector2(this.destination.X, this.destination.Z));

    /// <summary>着いたか（水平 range 以内・高さの差 8m 未満。飛んで向かうときは高さを見ない）。</summary>
    public bool ArrivedAt(Vector3 me, bool ignoreHeight)
        => this.FlatDistance(me) <= this.range && (ignoreHeight || MathF.Abs(me.Y - this.destination.Y) < 8f);

    /// <summary>1フレーム進める。</summary>
    /// <param name="ignoreHeight">高さを見ずに着いたとみなすか（巡回の点を上空で通り抜けるとき）。</param>
    public Result Tick(IFarmWorld world, INavControl nav, bool ignoreHeight)
    {
        if (!this.Active)
            return Result.Failed;

        var now = world.Now;
        var me = world.MyPosition;
        if (this.ArrivedAt(me, ignoreHeight))
        {
            this.Status = $"{this.label} に着きました";
            return Result.Arrived;
        }

        if (now - this.startedAt > this.limit)
            return this.Fail($"{this.label} へ {this.limit.TotalMinutes:0}分以内に着けませんでした（残り水平 {this.FlatDistance(me):0}m）");

        if (!nav.IsReady())
        {
            this.Status = "vnavmesh の地図の準備を待っています";
            return Result.Running;
        }

        // 前の自分の経路が止まったと確かめるまで、新しい経路を頼まない（R6）
        this.path.TickStop(nav);
        if (this.path.StopPending)
        {
            this.Status = this.path.StopProblem ?? "前の移動が止まったかを確かめています"; // 待った時間も移動の上限（limit）に入る
            return Result.Running;
        }

        if (!this.requested)
            return this.Begin(world, nav, now, me);

        // 動けなくなったかの見張り（経路を探している間は数えない）
        if (this.path.Searching)
            this.searchSince ??= now;
        else
            this.searchSince = null;
        var searchTooLong = this.searchSince is { } ss && now - ss > SearchLimit;

        var toGo = Vector3.Distance(me, this.destination);
        var left = nav.NumWaypoints();
        var consumed = left is { } l && this.waypointsLeft is { } prev && l < prev;
        this.waypointsLeft = left;
        if (toGo < this.closest - 1f || this.path.Searching || consumed)
        {
            this.closest = Math.Min(this.closest, toGo);
            this.closestAt = now;
        }

        var notCloser = now - this.closestAt > NotCloserLimit;
        if (!searchTooLong && !notCloser && (Vector3.Distance(me, this.progressAt) > 1f || this.path.Searching))
        {
            this.progressAt = me;
            this.progressSince = now;
        }
        else if (searchTooLong || notCloser || now - this.progressSince > StuckAfter)
        {
            if (++this.stuckCount > StuckLimit)
                return this.Fail($"{this.label} へ向かう途中で {StuckLimit} 回動けなくなりました（残り水平 {this.FlatDistance(me):0}m）");
            Core.DebugLog.Current?.Line("移動", $"{this.label} へ向かう途中、"
                + (searchTooLong ? $"経路探しが {SearchLimit.TotalSeconds:0}秒終わりません" : notCloser ? $"{NotCloserLimit.TotalSeconds:0}秒近づけません" : $"{StuckAfter.TotalSeconds:0}秒動けません")
                + $"（{this.stuckCount} 回目）。" + (world.Mounted ? "経路を引き直します" : "マウントに乗って飛んで行きます"));
            if (!world.Mounted)
                this.forceMount = true;
            this.path.Stop(nav);
            this.Restart(now, me);
            return Result.Running;
        }

        switch (this.path.Tick(nav, me, allowedToMove: true))
        {
            case OwnPath.State.Searching:
                this.Status = $"{this.label} への経路を探しています（残り水平 {this.FlatDistance(me):0}m）";
                return Result.Running;

            case OwnPath.State.Following:
                // 歩きの経路が途中で途切れていれば、乗って飛ぶ経路に切り替える（崖に走り込まない）
                if (!this.fly && !world.Mounted && !this.forceMount && this.path.LastGap > 3f && world.CanFlyHere && !world.InCombat)
                {
                    Core.DebugLog.Current?.Line("移動", $"{this.label} へは歩いて行けません（地上の経路が {this.path.LastGap:0}m 手前で途切れています）。乗って飛んで行きます");
                    this.path.Stop(nav);
                    this.forceMount = true;
                    this.Restart(now, me);
                    return Result.Running;
                }

                this.Status = $"{this.label} へ移動中（残り水平 {this.FlatDistance(me):0}m・{(this.fly ? "飛行" : world.Mounted ? "騎乗" : "徒歩")}）";
                return Result.Running;

            case OwnPath.State.Stale:
                this.Restart(now, me);
                this.Status = "動いた後なので経路を引き直します";
                return Result.Running;

            case OwnPath.State.NoPath:
            case OwnPath.State.Failed:
                if (++this.noPath >= NoPathLimit)
                    return this.Fail($"{this.label} への経路が {NoPathLimit} 回引けませんでした（残り水平 {this.FlatDistance(me):0}m）");
                if (!world.Mounted)
                    this.forceMount = true;
                this.Restart(now, me);
                return Result.Running;

            default:
                // 経路が終わったのに着いていない（他の処理が止めた・経路の終点が手前）。引き直す
                if (++this.noPath >= NoPathLimit)
                    return this.Fail($"{this.label} へ経路をたどっても着きませんでした（{NoPathLimit} 回。残り水平 {this.FlatDistance(me):0}m）");
                this.Restart(now, me);
                return Result.Running;
        }
    }

    private Result Begin(IFarmWorld world, INavControl nav, DateTime now, Vector3 me)
    {
        var far = this.FlatDistance(me) > MountOver || this.forceMount;

        // 倒した直後は数秒「戦闘中」が残り、その間は乗れない。乗る距離なら解けるのを待つ（8秒まで）
        if (far && !world.Mounted && world.InCombat)
        {
            this.combatWaitSince ??= now;
            if (now - this.combatWaitSince.Value < CombatClearWait)
            {
                this.Status = "戦闘中の状態が解けるのを待ってから、マウントに乗ります";
                return Result.Running;
            }
        }

        if (far && !world.Mounted && !world.InCombat && this.mountAttempts < MountAttemptLimit)
        {
            if (world.MountingInProgress || (this.mountIssuedAt is { } at && now - at < TimeSpan.FromSeconds(1.5)))
            {
                this.Status = "マウントに乗っています";
                return Result.Running;
            }

            if (!world.MountActionReady)
            {
                this.mountWaitSince ??= now;
                if (now - this.mountWaitSince.Value < TimeSpan.FromSeconds(5))
                {
                    this.Status = "マウントを使えるようになるのを待っています";
                    return Result.Running;
                }

                Core.DebugLog.Current?.Line("移動", "マウントが5秒使えないままなので歩きます");
                this.mountAttempts = MountAttemptLimit;
            }
            else if (world.CanMountNow)
            {
                this.mountAttempts++;
                this.mountIssuedAt = now;
                world.Mount();
                this.Status = "マウントに乗っています";
                return Result.Running;
            }
        }

        if (world.MountingInProgress)
            return Result.Running;

        this.fly = world.Mounted && world.CanFlyHere;
        // 歩くときは地面の点へ頼む（空中の行き先へは地上の経路が引けない）
        var to = !this.fly && this.walkTo is { } ground ? ground : this.destination;
        if (!this.path.Request(nav, me, to, this.fly))
            return this.Fail("vnavmesh の取り消せる経路探索（Nav.PathfindCancelable）を使えません（vnavmesh が読み込まれていないか、版が古い）");

        this.requested = true;
        this.progressAt = me;
        this.progressSince = now;
        this.closest = Vector3.Distance(me, this.destination);
        this.closestAt = now;
        this.Status = $"{this.label} への経路を探しています";
        return Result.Running;
    }

    private void Restart(DateTime now, Vector3 me)
    {
        this.requested = false;
        this.mountAttempts = 0;
        this.mountIssuedAt = null;
        this.mountWaitSince = null;
        this.combatWaitSince = null;
        this.searchSince = null;
        this.closest = float.MaxValue;
        this.closestAt = now;
        this.progressAt = me;
        this.progressSince = now;
    }

    private Result Fail(string why)
    {
        this.Problem = why;
        this.Status = why;
        this.Active = false;
        return Result.Failed;
    }

    /// <summary>止める（探索は取り消し、自分の経路なら止める。他人の経路は止めない）。止まったかは <see cref="TickStop"/> で確かめ続ける。</summary>
    public void Stop(INavControl nav)
    {
        this.path.Stop(nav);
        this.Active = false;
    }

    /// <summary>止めた自分の経路が止まったかを確かめる（動いていない間も毎フレーム呼ぶ）。</summary>
    public void TickStop(INavControl nav) => this.path.TickStop(nav);

    /// <summary>止めた自分の経路を、まだ止まったと確かめていないか。</summary>
    public bool StopPending => this.path.StopPending;

    /// <summary>止まったと確かめられない理由（無ければ null）。</summary>
    public string? StopProblem => this.path.StopProblem;

    /// <summary>止めずに手放す（次の移動・戦闘が新しい経路を渡すまで、そのまま進む）。</summary>
    public void Release()
    {
        this.path.Release();
        this.Active = false;
    }
}
