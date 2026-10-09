using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace AutoMobFarm.Farm;

/// <summary>
/// 1体の敵と戦う（実機で直した経過〔降りる場所の候補・視線・水の中・詰まり〕を含む）。
///
/// 狙った敵にこちらから近寄り、攻撃が届く距離になったら止まって RSR（Henched：ハードターゲットだけを殴る）に任せる。
/// 届くかはジョブの技の射程と視線をゲームに聞く（聞けないときは 3.5m 以内）。RSR の視線の判定で遮られていれば近づく。
///
/// 作りの決まり：
///  ・経路は取り消せる探索（OwnPath）だけを使う。使えなければ <see cref="Result.NavFailed"/>（SimpleMove は止めた後に遅れて歩き出すので使わない）。
///  ・攻撃してよい個体かの判定（TargetJudge）はここでは行わない。呼び出し側（FarmRunner）が毎フレーム引き直して判定してから渡す。
///
/// </summary>
public sealed class Engagement
{
    public enum Result
    {
        /// <summary>戦っている（または近づいている）。</summary>
        Running,

        /// <summary>HP が減らない・近づけない。この敵は諦める。</summary>
        Stalled,

        /// <summary>RSR に攻撃を任せられない。全体を止める。</summary>

        /// <summary>vnavmesh の取り消せる経路探索が使えない。全体を止める。</summary>
        NavFailed,
    }

    public const float FallbackReach = 3.5f;
    public const float DismountDistance = 20f;
    public const float MountApproachDistance = 40f;
    public const float FlyingLandDistance = 8f;
    public static readonly TimeSpan LandStuckAfter = TimeSpan.FromSeconds(2.5);
    public const int LandRelocationLimit = 3;
    public static readonly TimeSpan CloseInAfter = TimeSpan.FromSeconds(10);
    public const float CloseInDistance = 8f;

    private readonly StallWatch stall = new();
    private readonly OwnPath path = new() { DropCutOffEnd = true };
    private DateTime lastApproach = DateTime.MinValue;
    private DateTime dismountAt = DateTime.MinValue;
    private DateTime mountAt = DateTime.MinValue;

    private DateTime landSince = DateTime.MinValue;
    private DateTime landProgressAt = DateTime.MinValue;
    private Vector3 landProgressPos;
    private Vector3? landBlockedAt;
    private int landRelocations;

    private List<Vector3>? landCandidates;
    private Vector3 landCandidatesFor;
    private int landIndex;
    private float landTargetClosest = float.MaxValue;
    private DateTime landTargetClosestAt = DateTime.MinValue;
    private DateTime? overVoidAtTargetSince;

    private List<(Vector3 Pos, Task<List<Vector3>>? Ground)>? landSurvey;
    private DateTime landSurveySince;
    private bool landGroundOrdered;
    private bool landSkyChecked;

    public static readonly TimeSpan LandGroundWait = TimeSpan.FromSeconds(2.5);
    public const float SkyCheckDistance = 100f;
    public static readonly TimeSpan LandTargetStuckAfter = TimeSpan.FromSeconds(6);

    /// <summary>いま狙っている敵（ID）。</summary>
    public ulong TargetId { get; private set; }

    /// <summary>
    /// 攻撃してきた敵への反撃か（攻撃してきた敵は、倒してから乗る・移動する）。
    /// true なら、歩いて届かず乗れない（戦闘中）ときも Stalled にせず、歩ける所まで寄って相手が攻撃の届く所へ来るのを待つ（こちらを狙う敵は寄ってくる）。
    /// </summary>
    public bool HoldForAttacker { get; set; }

    // 「歩いて届かないので待つ」を記録に書いたか（相手ごとに 1 回）
    private bool holdLogged;

    /// <summary>自分が頼んだ近づく移動が動いているかもしれない。</summary>
    public bool ApproachIssued => this.path.Active;

    public Vector3? ApproachDestination { get; private set; }

    public string Problem { get; private set; } = string.Empty;

    /// <summary>諦めた理由（Stalled のとき。null なら「HP が 45 秒減っていない」）。</summary>
    public string? StallReason { get; private set; }

    public static readonly TimeSpan ApproachStuckAfter = TimeSpan.FromSeconds(8);

    private float approachClosest = float.MaxValue;
    private DateTime approachClosestAt = DateTime.MinValue;
    private bool jumpTried;

    private DateTime inReachSince = DateTime.MinValue;
    private bool closeIn;
    private ulong sightLoggedFor;

    /// <summary>攻撃が届く位置にいて、乗っていないか（画面の状態の出し分け用）。</summary>
    public bool InReachNow { get; private set; }

    /// <summary>
    /// このフレームで戦う段（降りて、自分の対象にハードターゲットを置く段）に入ったか。呼び出し側はこれが true で、ハードターゲットが
    /// 自分の対象だと読めたときだけ RSR に Henched を頼む（乗って近づいている間・水から移る間は攻撃させない）。
    /// </summary>
    public bool AttackPhase { get; private set; }

    /// <summary>狙い始める（狙い直す）。<paramref name="holdForAttacker"/>：攻撃してきた敵への反撃（<see cref="HoldForAttacker"/>）。</summary>
    public void Start(ICombatWorld world, IFoe foe, bool holdForAttacker = false)
    {
        this.HoldForAttacker = holdForAttacker;
        if (this.TargetId != foe.Id)
            this.holdLogged = false;
        this.TargetId = foe.Id;
        this.stall.Start(foe.Hp, world.Now);
        this.landSince = DateTime.MinValue;
        this.landBlockedAt = null;
        this.landRelocations = 0;
        this.StallReason = null;
        this.approachClosest = float.MaxValue;
        this.approachClosestAt = world.Now;
        this.landCandidates = null;
        this.jumpTried = false;
        this.inReachSince = DateTime.MinValue;
        this.closeIn = false;
        this.InReachNow = false;
    }

    private void NextLandCandidate(DateTime now, string why)
    {
        this.landIndex++;
        this.landTargetClosest = float.MaxValue;
        this.landTargetClosestAt = now;
        this.overVoidAtTargetSince = null;
        this.landSince = DateTime.MinValue;
        this.lastApproach = DateTime.MinValue;
        Core.DebugLog.Current?.Line("戦闘", $"降りる場所の候補を変えます（{why}。{this.landIndex + 1}/{this.landCandidates?.Count ?? 0} 番目へ）");
    }

    /// <summary>
    /// 飛んでいるときに降りる場所の候補：相手の真下の床と、周り 5m・10m・20m の8方向の、本来の地面とつながっている床で、
    /// 相手の床と同じ高さ（6m 以内）のもの。12か所まで。地図が使えなければ空。
    /// </summary>
    public static List<Vector3> LandingCandidates(INavControl nav, Vector3 foe, Vector3 me)
    {
        var list = new List<Vector3>();
        if (!nav.IsReady())
            return list;
        var baseY = nav.PointOnFloor(foe + new Vector3(0, 2f, 0), true, 2f)?.Y ?? foe.Y;

        void Add(Vector3? p)
        {
            if (p is { } v && MathF.Abs(v.Y - baseY) <= 6f && list.TrueForAll(x => Vector3.Distance(x, v) > 2f))
                list.Add(v);
        }

        Add(nav.PointOnFloor(foe + new Vector3(0, 2f, 0), false, 3f));
        var centerCount = list.Count;
        foreach (var r in new[] { 5f, 10f, 20f })
        {
            for (var k = 0; k < 8; k++)
            {
                var a = 2f * MathF.PI * k / 8;
                Add(nav.NearestPointReachable(new Vector3(foe.X + (MathF.Cos(a) * r), baseY, foe.Z + (MathF.Sin(a) * r)), 2f, 6f));
            }
        }

        var m2 = new Vector2(me.X, me.Z);
        var first = list.Take(centerCount);
        var rest = list.Skip(centerCount).OrderBy(p => Vector2.Distance(new Vector2(p.X, p.Z), m2));
        return first.Concat(rest).Take(12).ToList();
    }

    public enum GroundReach
    {
        Unknown,
        Walkable,
        CutOff,
    }

    public enum SkyKind
    {
        Unknown,
        Clear,
        Water,
        Overhead,
        Unlandable,
    }

    /// <summary>当たり判定の材質の「降りられない」（vnavmesh の SceneExtractor.ExtractMaterialFlags と同じ読み方）。</summary>
    public const ulong UnlandableMaterial = 0x200000;

    /// <summary>当たり判定の材質の「釣りができる面＝水面」（同上）。</summary>
    public const ulong FishableMaterial = 0x8000;

    public static GroundReach Ground(Task<List<Vector3>>? task)
    {
        if (task is not { IsCompletedSuccessfully: true })
            return GroundReach.Unknown;
        var p = task.Result;
        return p is { Count: > 0 } && OwnPath.GapOf(p) <= 3f ? GroundReach.Walkable : GroundReach.CutOff;
    }

    public static SkyKind Sky(Vector3 candidate, (float TopY, ulong Material)? top)
    {
        if (top is not { } t)
            return SkyKind.Unknown;
        if (t.TopY > candidate.Y + 2f)
            return SkyKind.Overhead;
        if (t.TopY < candidate.Y - 2f)
            return SkyKind.Unknown;
        if ((t.Material & UnlandableMaterial) != 0)
            return SkyKind.Unlandable;
        if ((t.Material & FishableMaterial) != 0)
            return SkyKind.Water;
        return SkyKind.Clear;
    }

    public static int Tier(GroundReach ground, SkyKind sky)
    {
        if (sky == SkyKind.Unlandable)
            return -1;
        var skyRank = sky switch { SkyKind.Clear => 0, SkyKind.Unknown => 1, SkyKind.Water => 2, _ => 3 };
        return ground switch
        {
            GroundReach.Walkable => skyRank,
            GroundReach.Unknown => 4 + (skyRank >= 2 ? 1 : 0),
            _ => 6 + (skyRank >= 2 ? 1 : 0),
        };
    }

    private void StartLandingSurvey(INavControl nav, Vector3 foe, DateTime now)
    {
        var goal = nav.NearestPointReachable(foe, 3f, 5f) ?? foe;
        this.landSurvey = this.landCandidates!
            .Select(c => (c, nav.TryPathfindCancelable(c, goal, false, CancellationToken.None, out var task) ? task : null))
            .ToList();
        this.landSurveySince = now;
        this.landGroundOrdered = this.landSurvey.All(x => x.Ground == null);
        this.landSkyChecked = false;
    }

    private void UpdateLandingSurvey(ICombatWorld world, Vector3 foe, DateTime now)
    {
        if (this.landSurvey == null || this.landCandidates == null)
            return;

        var reorder = false;
        if (!this.landGroundOrdered && (this.landSurvey.All(x => x.Ground is not { IsCompleted: false }) || now - this.landSurveySince > LandGroundWait))
        {
            this.landGroundOrdered = true;
            reorder = true;
        }

        var flat = Vector2.Distance(new Vector2(world.MyPosition.X, world.MyPosition.Z), new Vector2(foe.X, foe.Z));
        var sky = new Dictionary<Vector3, SkyKind>();
        if (!this.landSkyChecked && this.landGroundOrdered && flat <= SkyCheckDistance)
        {
            this.landSkyChecked = true;
            foreach (var c in this.landCandidates.Skip(this.landIndex))
                sky[c] = Sky(c, world.SkyAbove(c));
            reorder = sky.Values.Any(v => v != SkyKind.Unknown) || reorder;
        }

        if (!reorder)
            return;

        var ground = this.landSurvey.ToDictionary(x => x.Pos, x => Ground(x.Ground));
        var rest = this.landCandidates.Skip(this.landIndex)
            .Select((c, i) => (Pos: c, Index: i, Ground: ground.GetValueOrDefault(c), Sky: sky.GetValueOrDefault(c)))
            .Select(x => (x.Pos, x.Index, x.Ground, x.Sky, Tier: Tier(x.Ground, x.Sky)))
            .ToList();
        var ordered = rest.Where(x => x.Tier >= 0).OrderBy(x => x.Tier).ThenBy(x => x.Index).ToList();
        this.landCandidates = this.landCandidates.Take(this.landIndex).Concat(ordered.Select(x => x.Pos)).ToList();
    }

    /// <summary>待たされていた時間を「HP が減らない時間」に数えない。</summary>
    public void Resume(ICombatWorld world) => this.stall.Resume(world.Now);

    /// <summary>狙いを外す（ハードターゲットがこの敵なら外す。他人が置いたターゲットは外さない）。</summary>
    public void Forget(ICombatWorld world)
    {
        if (this.TargetId != 0 && world.HardTargetId == this.TargetId)
            world.SetHardTarget(null);
        this.TargetId = 0;
        this.InReachNow = false;
        this.AttackPhase = false;
    }

    /// <summary>1フレーム戦う。</summary>
    /// <param name="yieldMovement">
    /// BMR に足を任せている戦う段で、BMR が動かしたがっている・いまの位置が範囲攻撃の中（BmrAssist.YieldMovement）。
    /// true の間は、戦う段（降りた後）でこちらの近づく移動を出さない（vnavmesh が経路を走っている間は BMR が足を上書きしないので、
    /// こちらが止めないと範囲攻撃の中へ歩いてしまう）。
    /// </param>
    public Result Tick(ICombatWorld world, ICombatPermit permit, INavControl nav, IFoe t, out string status, bool yieldMovement = false)
    {
        var now = world.Now;
        var dist = Vector3.Distance(world.MyPosition, t.Position);
        status = $"{t.Name} と戦闘中（{dist:0.0}m）";
        this.InReachNow = false;
        this.AttackPhase = false;

        if (this.stall.Observe(t.Hp, now))
        {
            this.StopApproach(nav);
            this.StallReason = null;
            return Result.Stalled;
        }

        var reach = world.InReach(t, out var code);
        var inReach = reach ?? dist <= FallbackReach;
        var flat = Vector2.Distance(new Vector2(world.MyPosition.X, world.MyPosition.Z), new Vector2(t.Position.X, t.Position.Z));

        if (inReach && world.RsrSightBlocked(t) is { } sightBlock)
        {
            inReach = false;
            if (this.sightLoggedFor != t.Id)
            {
                this.sightLoggedFor = t.Id;
                Core.DebugLog.Current?.Line("戦闘", $"{t.Name}（{dist:0.0}m）はゲームの判定では攻撃が届きますが、RSR の視線の判定が "
                    + $"({sightBlock.Point.X:0.0}, {sightBlock.Point.Y:0.0}, {sightBlock.Point.Z:0.0})（材質 0x{sightBlock.Material:X}）で遮られるので、見える所まで近づきます");
            }
        }

        if (!inReach || world.Mounted)
            this.inReachSince = DateTime.MinValue;
        else if (this.inReachSince == DateTime.MinValue)
            this.inReachSince = now;
        if (!this.closeIn && this.inReachSince != DateTime.MinValue && flat > CloseInDistance
            && now - this.inReachSince > CloseInAfter && this.stall.SinceProgress(now) > CloseInAfter)
        {
            this.closeIn = true;
            Core.DebugLog.Current?.Line("戦闘", $"{t.Name}（{dist:0.0}m）は攻撃が届く位置ですが、HP が {CloseInAfter.TotalSeconds:0} 秒減らないので、水平 {CloseInDistance:0}m まで近寄ります");
        }

        if (this.closeIn && flat > CloseInDistance)
            inReach = false;

        var overVoid = world.Mounted && world.Flying && !GroundBelowLikeFoe(nav, world.MyPosition, t.Position);
        var near = inReach || dist < DismountDistance || flat < DismountDistance;

        Vector3? landAt = null;
        if (world.Mounted && world.Flying)
        {
            if (this.landCandidates == null || Vector3.Distance(this.landCandidatesFor, t.Position) > 6f)
            {
                this.landCandidates = LandingCandidates(nav, t.Position, world.MyPosition);
                this.landCandidatesFor = t.Position;
                this.landIndex = 0;
                this.landTargetClosest = float.MaxValue;
                this.landTargetClosestAt = now;
                this.overVoidAtTargetSince = null;
                this.StartLandingSurvey(nav, t.Position, now);
            }

            this.UpdateLandingSurvey(world, t.Position, now);

            if (this.landCandidates.Count > 0)
            {
                if (this.landIndex >= this.landCandidates.Count)
                {
                    this.StopApproach(nav);
                    this.StallReason = $"の近くに降りられる場所が見つかりません（vnavmesh の床の候補 {this.landCandidates.Count} か所のどこにも、飛んで届かないか降りられませんでした）。";
                    return Result.Stalled;
                }

                landAt = this.landCandidates[this.landIndex];
                var toLand = Vector2.Distance(new Vector2(world.MyPosition.X, world.MyPosition.Z), new Vector2(landAt.Value.X, landAt.Value.Z));
                if (toLand < this.landTargetClosest - 1f)
                {
                    this.landTargetClosest = toLand;
                    this.landTargetClosestAt = now;
                }

                if (toLand > 2.5f && now - this.landTargetClosestAt > LandTargetStuckAfter)
                    this.NextLandCandidate(now, $"飛んで {LandTargetStuckAfter.TotalSeconds:0}秒近づけません（あと水平 {toLand:0.0}m）");
                else if (toLand <= 2.5f && overVoid)
                {
                    this.overVoidAtTargetSince ??= now;
                    if (now - this.overVoidAtTargetSince.Value > TimeSpan.FromSeconds(3))
                        this.NextLandCandidate(now, "真上に来ても足もとに床が見つかりません");
                }
                else
                {
                    this.overVoidAtTargetSince = null;
                }

                landAt = this.landIndex < this.landCandidates.Count ? this.landCandidates[this.landIndex] : null;
            }
        }

        var overLanding = landAt is { } la
            ? Vector2.Distance(new Vector2(world.MyPosition.X, world.MyPosition.Z), new Vector2(la.X, la.Z)) <= 2.5f
            : flat <= FlyingLandDistance;
        var haveCandidates = world.Flying && this.landCandidates is { Count: > 0 };

        var onCandidate = world.Mounted && !world.Flying && this.landCandidates is { Count: > 0 } lc && this.landIndex < lc.Count
                          && Vector2.Distance(new Vector2(world.MyPosition.X, world.MyPosition.Z), new Vector2(lc[this.landIndex].X, lc[this.landIndex].Z)) <= 4f;
        var wantLand = world.Mounted && !overVoid
                       && (world.Flying
                           ? overLanding || (!haveCandidates && near && !nav.IsMoving() && this.lastApproach != DateTime.MinValue)
                           : near || onCandidate);

        if (world.Flying && haveCandidates && !this.landGroundOrdered)
            wantLand = false;

        if (this.landBlockedAt is { } blocked)
        {
            var away = Vector2.Distance(new Vector2(world.MyPosition.X, world.MyPosition.Z), new Vector2(blocked.X, blocked.Z));
            if (!world.Mounted || flat <= 3f || (away >= 5f && !nav.IsMoving()))
                this.landBlockedAt = null;
            else
                wantLand = false;
        }

        if (wantLand && world.Swimming)
        {
            wantLand = false;
            if (this.landCandidates is { Count: > 0 } && this.landIndex < this.landCandidates.Count && now - this.landTargetClosestAt > TimeSpan.FromSeconds(1))
            {
                this.NextLandCandidate(now, "水の中です（泳いでいる。降りると攻撃できない）");
                this.lastApproach = DateTime.MinValue;
            }

            status = $"{t.Name} の近くですが、水の中なので降りずに、別の降りる場所へ移ります";
            if (this.landCandidates is { Count: > 0 } lcs && this.landIndex >= lcs.Count)
            {
                this.StopApproach(nav);
                this.StallReason = "の近くに、水の中でない降りられる場所が見つかりません。";
                return Result.Stalled;
            }
        }

        if (wantLand)
        {
            this.approachClosestAt = now;
            this.StopApproach(nav);

            if (this.landSince == DateTime.MinValue)
            {
                this.landSince = now;
                this.landProgressAt = now;
                this.landProgressPos = world.MyPosition;
            }
            else if (Vector3.Distance(world.MyPosition, this.landProgressPos) >= 0.3f)
            {
                this.landProgressAt = now;
                this.landProgressPos = world.MyPosition;
            }
            else if (now - this.landProgressAt >= LandStuckAfter && this.landCandidates is { Count: > 0 } && world.Flying
                     && (this.dismountAt >= this.landSince || now - this.landSince >= TimeSpan.FromSeconds(5)))
            {
                this.NextLandCandidate(now, "降りる操作を送っても位置が動きません（物の上など）");
                status = $"{t.Name} の近くですが、ここでは降りられないので、別の降りる場所へ移ります（{this.landIndex + 1}/{this.landCandidates.Count} 番目）";
                return Result.Running;
            }
            else if (now - this.landProgressAt >= LandStuckAfter && this.landRelocations < LandRelocationLimit
                     && (this.dismountAt >= this.landSince || now - this.landSince >= TimeSpan.FromSeconds(5)))
            {
                this.landRelocations++;
                this.landBlockedAt = world.MyPosition;
                this.landSince = DateTime.MinValue;
                this.lastApproach = DateTime.MinValue;
                status = $"{t.Name} の近くですが、ここでは降りられないので、相手のそばまで動いてから降り直します（{this.landRelocations}/{LandRelocationLimit} 回目）";
                return Result.Running;
            }

            if (world.DismountReady && now - this.dismountAt >= TimeSpan.FromSeconds(0.5))
            {
                this.dismountAt = now;
                world.Dismount();
            }

            status = $"{t.Name} の近くなのでマウントから降ります（水平 {flat:0.0}m）";
            return Result.Running;
        }

        this.landSince = DateTime.MinValue;

        // 攻撃を許してはいけない問題（RSR の設定を読めない・反映しない・外から変わった）が出たら、ターゲットを置く前に止める
        if (permit.Problem is { } tp)
        {
            this.StopApproach(nav);
            this.stall.Resume(now);
            status = $"{t.Name}：RSR の設定を戻すのを待っています（{tp}）";
            return Result.Running;
        }

        // 戦う段に入った。ハードターゲットを自分の対象に置く（RSR はまだ Off なので、置いただけでは攻撃しない）。
        // 呼び出し側は、ハードターゲットが自分の対象になったと読めたときだけ Henched を頼み、Henched と範囲攻撃 Off などを読み直してから
        // 攻撃可になる（AttackReady）。Henched は「ハードターゲットを殴る」モードなので、先に別の敵が狙われたまま Henched にしない
        this.AttackPhase = true;
        if (world.HardTargetId != t.Id)
            world.SetHardTarget(t);
        var ready = permit.AttackReady;

        if (!world.Mounted && world.Swimming && world.CanMountNow)
        {
            this.approachClosestAt = now;
            if (now - this.mountAt >= TimeSpan.FromSeconds(2))
            {
                if (this.landCandidates is { Count: > 0 } && this.landIndex < this.landCandidates.Count)
                    this.NextLandCandidate(now, "降りた所が水の中でした（泳いでいる）");
                this.mountAt = now;
                this.StopApproach(nav);
                world.Mount();
            }

            status = $"{t.Name} の近くですが、水の中では戦えないので、乗って別の場所へ移ります";
            return Result.Running;
        }

        if (inReach && !world.Mounted)
        {
            this.approachClosestAt = now;
            this.StopApproach(nav);
            this.InReachNow = ready;
            status = ready
                ? $"{t.Name} に攻撃が届く位置です（{dist:0.0}m。攻撃は RSR に任せています）"
                : $"{t.Name} に攻撃が届く位置です（{dist:0.0}m）。RSR が Henched になり設定が確かめられるのを待っています";
            return Result.Running;
        }

        // BMR に足を任せている間（範囲攻撃を避けている・相手へ寄っている・いまの位置が範囲攻撃の中）は、こちらの近づく移動を出さない
        if (yieldMovement && !world.Mounted)
        {
            this.approachClosestAt = now; // 任せている間を「近づけない」に数えない（乗る・跳ぶの立て直しへ進まない）
            this.StopApproach(nav);
            status = $"{t.Name}：BMR が動かしています（範囲攻撃を避ける・相手へ寄る。{dist:0.0}m）";
            return Result.Running;
        }

        if (world.Casting)
        {
            this.approachClosestAt = now;
            status = $"{t.Name} へ詠唱中です（{dist:0.0}m）";
            return Result.Running;
        }

        if (!world.Mounted && flat > MountApproachDistance && world.CanMountNow)
        {
            this.approachClosestAt = now;
            if (now - this.mountAt >= TimeSpan.FromSeconds(2))
            {
                this.mountAt = now;
                this.StopApproach(nav);
                world.Mount();
            }

            status = $"{t.Name} は遠い（水平 {flat:0}m）ので、マウントに乗って近づきます";
            return Result.Running;
        }

        var flyPath = world.Flying || (world.Mounted && world.CanFlyHere);

        if (flat < this.approachClosest - 1f)
        {
            this.approachClosest = flat;
            this.approachClosestAt = now;
        }

        var cutOff = !flyPath && this.path.Active && this.path.LastGap > 3f;
        var holding = false;

        if (!cutOff && !world.Mounted && !flyPath && !this.jumpTried && now - this.approachClosestAt > ApproachStuckAfter)
        {
            this.jumpTried = true;
            this.approachClosestAt = now;
            world.Jump();
            status = $"{t.Name} へ近づけないので、ジャンプして抜けます";
            return Result.Running;
        }

        if (cutOff || now - this.approachClosestAt > ApproachStuckAfter)
        {
            if (!world.Mounted && world.CanMountNow)
            {
                if (now - this.mountAt >= TimeSpan.FromSeconds(2))
                {
                    if (this.landCandidates is { Count: > 0 } && this.landIndex < this.landCandidates.Count)
                        this.NextLandCandidate(now, "降りた所から歩いて近づけません");
                    this.mountAt = now;
                    this.StopApproach(nav);
                    world.Mount();
                    this.approachClosestAt = now;
                }

                status = $"{t.Name} へは歩いて近づけないので、マウントに乗って飛んで近づきます";
                return Result.Running;
            }

            if (!flyPath && this.HoldForAttacker)
            {
                // 攻撃してきた敵は倒すまで離れない。歩いて届かず、戦闘中で乗れないときは諦めずに、途切れた経路の手前（歩ける所）まで寄り、
                // 相手が攻撃の届く所へ来るのを待つ（経路は止めない。たどり終えたら下で頼み直す。届けば上の「攻撃が届く位置」で止まって戦う）
                holding = true;
                if (!this.holdLogged)
                {
                    this.holdLogged = true;
                    Core.DebugLog.Current?.Line("戦闘", $"{t.Name}（{dist:0.0}m）へは"
                        + (cutOff ? $"歩いて届きません（vnavmesh の経路が {this.path.LastGap:0}m 手前で途切れています）" : $"{ApproachStuckAfter.TotalSeconds:0}秒近づけません")
                        + "。戦闘中で乗れないので、歩ける所まで寄って、相手が攻撃の届く所へ来るのを待ちます（倒すまで離れません）");
                }
            }
            else if (!flyPath)
            {
                this.StopApproach(nav);
                this.StallReason = cutOff
                    ? $"へは歩いて行けません（vnavmesh の経路が {this.path.LastGap:0}m 手前で途切れています）。乗れないので"
                    : $"に {ApproachStuckAfter.TotalSeconds:0}秒近づけません。乗れないので";
                return Result.Stalled;
            }
        }

        var moving = this.path.Tick(nav, world.MyPosition, allowedToMove: true) is OwnPath.State.Searching or OwnPath.State.Following;
        var moved = this.ApproachDestination is { } d && Vector3.Distance(d, t.Position) > 6f;
        if (this.path.StopPending)
        {
            // 前の自分の経路が止まったと確かめるまで、新しい経路を頼まない（R6）
            status = this.path.StopProblem ?? $"{t.Name} へ向かう前に、前の移動が止まったかを確かめています";
            return Result.Running;
        }

        if ((!moving || moved) && now - this.lastApproach > TimeSpan.FromSeconds(1))
        {
            this.lastApproach = now;
            var goal = landAt ?? (flyPath ? null : nav.NearestPointReachable(t.Position, 3f, 5f)) ?? t.Position;
            if (this.path.Request(nav, world.MyPosition, goal, flyPath))
            {
                this.ApproachDestination = t.Position;
            }
            else
            {
                this.StopApproach(nav);
                this.Problem = "vnavmesh の取り消せる経路探索（Nav.PathfindCancelable）を使えません（vnavmesh が読み込まれていないか、版が古い）";
                return Result.NavFailed;
            }
        }

        status = holding
            ? $"{t.Name} へは歩いて届かず乗れないので、寄れる所まで寄って、相手が来るのを待っています（{dist:0.0}m）"
            : this.landBlockedAt != null
            ? $"{t.Name} の近くですが、ここでは降りられなかったので、相手のそばまで動いています（水平 {flat:0.0}m）"
            : overVoid
            ? $"{t.Name} の近くですが、足もとに降りられる床が無いので、床の上まで飛んで近づきます（水平 {flat:0.0}m）"
            : reach == false
                ? $"{t.Name} に近づいています（{dist:0.0}m・{(code == 562 ? "見えない位置" : "射程外")}）"
                : $"{t.Name} に近づいています（{dist:0.0}m）";
        return Result.Running;
    }

    /// <summary>真下（水平 1.5m 以内）に、相手のいる床と同じ高さ（8m 以内）の床があるか。地図の準備ができていなければ true。</summary>
    public static bool GroundBelowLikeFoe(INavControl nav, Vector3 me, Vector3 foe)
    {
        if (!nav.IsReady())
            return true;
        if (nav.PointOnFloor(me + new Vector3(0, 2f, 0), true, 1.5f) is not { } mine)
            return false;
        if (nav.PointOnFloor(foe + new Vector3(0, 2f, 0), true, 2f) is { } foeFloor)
            return MathF.Abs(mine.Y - foeFloor.Y) <= 8f;
        return mine.Y >= foe.Y - 20f && mine.Y <= foe.Y + 5f;
    }

    /// <summary>自分が頼んだ近づく移動を止める（探索は取り消し、自分の経路なら止める。他人の経路は止めない）。</summary>
    public void StopApproach(INavControl nav) => this.path.Stop(nav);

    /// <summary>止めた近づく移動が止まったかを確かめる（戦っていない間も毎フレーム呼ぶ）。</summary>
    public void TickStop(INavControl nav) => this.path.TickStop(nav);

    /// <summary>止めた近づく移動を、まだ止まったと確かめていないか。</summary>
    public bool StopPending => this.path.StopPending;

    /// <summary>止まったと確かめられない理由（無ければ null）。</summary>
    public string? StopProblem => this.path.StopProblem;
}
