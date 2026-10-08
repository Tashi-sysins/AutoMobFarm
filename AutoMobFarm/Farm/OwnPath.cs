using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace AutoMobFarm.Farm;

/// <summary>
/// 自分が頼んだ経路探索と、その経路の追従。
/// 止めた後・行き先を変えた後・読み込みを解除した後に、古い探索の結果で歩き出さない（遅れて返った経路を実行しない）。
///
/// Nav.PathfindCancelable で経路だけを求め、こちらが <see cref="Tick"/> で結果を受け取って、まだ使ってよいときだけ Path.MoveTo で歩かせる。
/// 止めたら結果は捨てる。
/// 注意（ffxiv_navmesh の AsyncMoveRequest.cs・FollowPath.cs で確かめた事実）：
///  ・頼んだのと同じフレームで取り消すと、vnavmesh の探索件数が戻らず Nav.PathfindInProgress が true のまま残る。同じフレームのうちは捨てるだけ。
///  ・取り消した探索は、すぐ終わるとは限らない。終わるのを待たない。
///  ・経路の最初の点は、頼んだときの出発点。捨ててから渡す。探索の間に経路の上を進んでいたら、通り過ぎた点も捨てる。
///  ・探索の間に経路から離れていたら、その経路は使わない（引き直す）。
///  ・新しい探索の間も、前に渡した経路は歩き続ける。止めるときは自分の経路（終点で見分ける）だけ止める。他人の経路は止めない。
/// </summary>
public sealed class OwnPath
{
    public enum State
    {
        Idle,
        Searching,
        Following,
        NoPath,
        Failed,
        Stale,
    }

    /// <summary>フレームの番号（プラグインが毎フレーム1つ進める。試験では直接動かす）。</summary>
    public static long Frame { get; set; }

    /// <summary>探索の結果の経路から、いまの位置がこれ以上離れていたら結果を使わない（m）。</summary>
    public const float StaleDistance = 5f;

    private CancellationTokenSource? cts;
    private Task<List<Vector3>>? task;
    private long issuedFrame;
    private Vector3 from;
    private bool fly;
    private Vector3? followingEnd;

    // 止めずに手放した経路の終点（手放した後に止めるとき、それがまだ自分の経路なら止める）
    private Vector3? releasedEnd;

    /// <summary>探索中か、自分の経路を渡した後か。</summary>
    public bool Active => this.task != null || this.followingEnd != null;

    /// <summary>探索中か。</summary>
    public bool Searching => this.task != null;

    /// <summary>実際に渡した経路の終点。</summary>
    public Vector3? FollowingEnd => this.followingEnd;

    /// <summary>歩きの経路の最後の2点がこれより離れていたら、最後の区間（壁・崖を貫く直線）を捨てる。</summary>
    public const float DropGap = 8f;

    /// <summary>途切れた歩きの経路の最後の区間を捨てるか。</summary>
    public bool DropCutOffEnd { get; init; }

    /// <summary>最後に受け取った経路の、最後の2点の隔たり（地上の経路が途中までしか引けなかった目安）。</summary>
    public float LastGap { get; private set; }

    /// <summary>経路の最後の区間の長さ（3点以上の経路だけ。2点以下は 0）。</summary>
    public static float GapOf(IReadOnlyList<Vector3> points) => points.Count >= 3 ? Vector3.Distance(points[^2], points[^1]) : 0f;

    /// <summary>最後に渡した経路の長さ。</summary>
    public float RouteLength { get; private set; }

    /// <summary>探索を頼む（前の探索は取り消す・捨てる）。取り消せる探索の窓口が使えなければ false。前に渡した経路は止めない。</summary>
    public bool Request(INavControl nav, Vector3 start, Vector3 destination, bool flying)
    {
        this.Abandon();
        var source = new CancellationTokenSource();
        if (!nav.TryPathfindCancelable(start, destination, flying, source.Token, out var t) || t == null)
        {
            source.Dispose();
            return false;
        }

        this.cts = source;
        this.task = t;
        this.issuedFrame = Frame;
        this.from = start;
        this.fly = flying;
        return true;
    }

    /// <summary>1フレーム見る。探索が終わっていて、使ってよければ経路を渡す。</summary>
    /// <param name="allowedToMove">いま歩き出してよいか（止める途中などは false：結果は捨てる）。</param>
    public State Tick(INavControl nav, Vector3 now, bool allowedToMove)
    {
        this.TickStop(nav);
        if (this.task is { } t)
        {
            if (!t.IsCompleted)
                return State.Searching;

            this.task = null;
            this.DisposeSource();
            if (t.IsFaulted || t.IsCanceled)
            {
                _ = t.Exception;
                return State.Failed;
            }

            if (!allowedToMove)
                return State.Idle;

            // 前の自分の経路が止まったと確かめる前に、別の経路を渡さない（R6：停止確認前に別経路を始めない）
            if (this.StopPending)
                return State.Stale;

            var points = t.Result;
            if (points == null || points.Count == 0)
                return State.NoPath;
            this.LastGap = GapOf(points);

            if (this.DropCutOffEnd && !this.fly && this.LastGap > DropGap)
                points = points.Take(points.Count - 1).ToList();

            var route = Vector3.Distance(now, this.from) <= StaleDistance
                ? (points.Count > 1 ? points.Skip(1).ToList() : points)
                : AheadOf(points, this.from, now, StaleDistance);
            if (route == null)
                return State.Stale;
            if (!nav.MoveAlong(route, this.fly))
                return State.Failed;

            var length = 0f;
            var at = now;
            foreach (var p in route)
            {
                length += Vector3.Distance(at, p);
                at = p;
            }

            this.RouteLength = length;
            this.followingEnd = route[^1];
            this.releasedEnd = null; // 新しい経路で上書きした
            return State.Following;
        }

        if (this.followingEnd is { } end)
        {
            // 読めないときは「自分の経路のまま」とみなす（「たどり終えた」と取り違えて次の経路を重ねない）
            if (this.IsMine(nav, end) != false)
                return State.Following;
            this.followingEnd = null;
        }

        return State.Idle;
    }

    /// <summary>経路のうち、いまの位置より先の点。どの区間からも maxOff より離れていれば null。</summary>
    public static List<Vector3>? AheadOf(IReadOnlyList<Vector3> points, Vector3 from, Vector3 now, float maxOff)
    {
        if (points.Count == 0)
            return null;
        var pts = points.Count == 1 ? new List<Vector3> { from, points[0] } : points;

        var dists = new float[pts.Count - 1];
        var best = float.MaxValue;
        for (var i = 0; i < dists.Length; i++)
        {
            dists[i] = DistanceToSegment(now, pts[i], pts[i + 1]);
            best = MathF.Min(best, dists[i]);
        }

        if (best > maxOff)
            return null;

        var idx = Array.FindIndex(dists, d => d <= best + 1f);
        return pts.Skip(idx + 1).ToList();
    }

    private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var len = ab.LengthSquared();
        var t = len <= 0 ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / len, 0f, 1f);
        return Vector3.Distance(p, a + (ab * t));
    }

    /// <summary>止める：探索は取り消す（同じフレームなら捨てるだけ）。自分の経路をたどっていれば止める。他人の経路は止めない。</summary>
    public void Stop(INavControl nav)
    {
        this.Abandon();
        foreach (var e in new[] { this.followingEnd, this.releasedEnd })
            if (e is { } v && !this.stopPending.Any(p => Vector3.Distance(p, v) < 0.01f))
                this.stopPending.Add(v);
        this.followingEnd = null;
        this.releasedEnd = null;
        if (this.stopPending.Count == 0)
            return;

        if (this.stopSince == DateTime.MinValue)
            this.stopSince = Clock();
        this.lastStopSent = DateTime.MinValue;
        this.TickStop(nav);
    }

    /// <summary>
    /// 止めると決めた自分の経路が止まったかを確かめる（毎フレーム呼ぶ。R6）。
    ///  ・vnavmesh が動いていない、または終点が自分の経路と違う（他人の経路に変わった）→ 止まったとして控えを消す
    ///  ・まだ自分の経路をたどっている → 止める命令を 0.5 秒おきに送り直す（受け付けたことは止まった証拠にしない）
    ///  ・読めない → 「分からない」として控えを残す（「自分の経路ではない」と取り違えない）
    /// <see cref="StopConfirmLimit"/> を過ぎても確かめられなければ <see cref="StopProblem"/> に理由を出す（送り直しは続ける）。
    /// </summary>
    public void TickStop(INavControl nav)
    {
        if (this.stopPending.Count == 0)
            return;
        var now = Clock();
        var mine = this.IsMineAny(nav, this.stopPending);
        if (mine == false)
        {
            this.stopPending.Clear();
            this.stopSince = DateTime.MinValue;
            this.StopProblem = null;
            return;
        }

        if (mine == true && now - this.lastStopSent >= StopResendInterval)
        {
            this.lastStopSent = now;
            if (!nav.Stop())
                Core.DebugLog.Current?.Line("移動", "vnavmesh の Path.Stop を送れませんでした（送り直します）");
        }

        if (now - this.stopSince > StopConfirmLimit && this.StopProblem == null)
        {
            this.StopProblem = mine == null
                ? $"vnavmesh の状態を読めないので、移動を止めたと {StopConfirmLimit.TotalSeconds:0}秒確かめられません（止める命令は送り続けます）"
                : $"vnavmesh に止める命令を送っても、自分の経路が {StopConfirmLimit.TotalSeconds:0}秒止まりません（送り続けます）";
            Core.DebugLog.Current?.Line("移動", this.StopProblem);
        }
    }

    /// <summary>止めると決めた自分の経路を、まだ止まったと確かめていないか（確かめるまで別の経路を始めない）。</summary>
    public bool StopPending => this.stopPending.Count > 0;

    /// <summary>止めたと確かめられないときの理由（画面用。確かめられれば null）。</summary>
    public string? StopProblem { get; private set; }

    /// <summary>止めたと確かめるまでの上限（過ぎたら理由を出す）。</summary>
    public static readonly TimeSpan StopConfirmLimit = TimeSpan.FromSeconds(5);

    /// <summary>止める命令を送り直す間隔。</summary>
    public static readonly TimeSpan StopResendInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>時刻（試験で差し替える）。</summary>
    public static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private readonly List<Vector3> stopPending = [];
    private DateTime stopSince = DateTime.MinValue;
    private DateTime lastStopSent = DateTime.MinValue;

    /// <summary>探索を捨て、渡した経路は止めずに手放す（次の移動が新しい経路を渡すまで、そのまま進む）。止めるときは手放した経路も止める。</summary>
    public void Release()
    {
        this.Abandon();
        if (this.followingEnd is { } end)
            this.releasedEnd = end;
        this.followingEnd = null;
    }

    /// <summary>いま vnavmesh がたどっている経路が、この終点の自分の経路か（読めなければ null）。</summary>
    private bool? IsMine(INavControl nav, Vector3 end) => this.IsMineAny(nav, [end]);

    private bool? IsMineAny(INavControl nav, IReadOnlyList<Vector3> ends)
    {
        var following = nav.FollowingPath();
        if (following == null)
            return null;
        if (following == false)
            return false;
        if (!nav.TryLastWaypoint(out var w))
            return null;
        return w is { } v && ends.Any(e => Vector3.Distance(v, e) < 1f);
    }

    private void Abandon()
    {
        if (this.task is not { } t)
            return;

        this.task = null;
        var source = this.cts;
        this.cts = null;

        t.ContinueWith(done =>
        {
            _ = done.Exception;
            source?.Dispose();
        }, TaskScheduler.Default);

        if (Frame != this.issuedFrame)
        {
            try
            {
                source?.Cancel();
            }
            catch
            {
                // すでに片づいている
            }
        }
    }

    private void DisposeSource()
    {
        try
        {
            this.cts?.Dispose();
        }
        catch
        {
            // 片づけの失敗は無視する
        }

        this.cts = null;
    }
}

/// <summary>戦闘の停滞の見張り：最後に HP が減った時刻から数える（回復で増えたときは進展にしない）。</summary>
public sealed class StallWatch
{
    private DateTime lastProgress;
    private uint lastHp;

    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(45);

    public void Start(uint hp, DateTime now)
    {
        this.lastHp = hp;
        this.lastProgress = now;
    }

    /// <summary>待たされていた時間を数えない。</summary>
    public void Resume(DateTime now) => this.lastProgress = now;

    public TimeSpan SinceProgress(DateTime now) => now - this.lastProgress;

    /// <summary>いまの HP を見る。最後に減ってから <see cref="Limit"/> を過ぎていたら true。</summary>
    public bool Observe(uint hp, DateTime now)
    {
        if (hp < this.lastHp)
            this.lastProgress = now;
        this.lastHp = hp;
        return now - this.lastProgress > Limit;
    }
}
