using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoMobFarm.Judge;

namespace AutoMobFarm.Farm;

/// <summary>収集の状態。</summary>
public enum FarmState
{
    /// <summary>待機（始めていない）。</summary>
    Idle,

    /// <summary>戦闘連携の準備（RSR の控えを取り、Off で攻撃を抑止し、範囲攻撃 Off などを読み直して確かめる）。時間では止めず、確かめられるまで待つ。</summary>
    Arm,

    /// <summary>エリア移動（テレポ）。</summary>
    Travel,

    /// <summary>vnavmesh の地図の準備待ち。準備ができたら狩り場の点の高さを決めて巡回の計画を作る。</summary>
    NavWait,

    /// <summary>狩り場（巡回の点）へ移動。移動しながら索敵もする（着く前に見つければ狙う）。</summary>
    ToSpot,

    /// <summary>索敵・対象判定（巡回の点に着いた）。</summary>
    Search,

    /// <summary>
    /// 接近・討伐（Engagement）。討伐対象と戦うときと、攻撃してきた敵に反撃するとき。攻撃を許すのはこの状態で、
    /// 相手にハードターゲットを置き、RSR の Henched を読み直した後だけ。
    /// </summary>
    Engage,

    /// <summary>所持数確認（倒した直後）。</summary>
    CountCheck,

    /// <summary>再出現待ち（回る点が1つだけのとき）。</summary>
    SpawnWait,

    /// <summary>
    /// 条件待ち（予定外のエリア移動・自分を読めない・戦闘不能・コンテンツ/リーヴ・戦闘ジョブでない・RSR が無い）。何もせず待ち、
    /// 条件が消えたら準備からやり直す（危険を見つけても止めない）。コンテンツ・リーヴの間は RSR を元の設定へ戻す。
    /// </summary>
    Standby,

    /// <summary>完了（目標数に達した）。</summary>
    Done,

    /// <summary>停止（理由つき）。</summary>
    Stopped,
}

/// <summary>止まった理由（構造のまま持つ：画面で使えるように）。<paramref name="RsrHeld"/>：RSR を Off のまま保持したか。</summary>
public sealed record StopInfo(string Code, string Message, DateTime At, IReadOnlyList<string> Details, bool RsrHeld = false);

/// <summary>
/// 収集の計画（1エリア）。<paramref name="Areas"/> がこの実行でこちらから仕掛けてよい狩り場の集合（カタログの地点から作る）で、
/// 計画に無い名前・エリアの敵には仕掛けない（攻撃してきた敵への反撃は別）。
/// <paramref name="AdHoc"/>＝座標のクリックで始めた「その場の討伐」（品の目標なし。ItemId 0・TargetCount 0・ItemName は地点の名前）。
/// </summary>
public sealed record FarmPlan(uint ItemId, string ItemName, int TargetCount, uint Territory, IReadOnlyList<HuntArea> Areas, bool AdHoc = false);

/// <summary>期限など（試験で短くできるように外に出した）。時間で止めるのは「進展なし」だけ（止めずに立て直す）。</summary>
public sealed class FarmLimits
{
    /// <summary>
    /// 選んだ品の所持数が増えないまま、この時間たったら諦めて次へ（長時間の進展なし）。敵を倒しただけでは延ばさない。
    /// その場の討伐は品が無いので、討伐対象を倒したことを進展に数える。条件待ちの時間は数えない。
    /// </summary>
    public TimeSpan NoProgress { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>準備（RSR の確かめ）が長引いたとき、足りない物を記録に書く間隔（止めない）。</summary>
    public TimeSpan ArmNotice { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>狩り場のエリアへ移れないまま、この時間たったら、このエリアを諦めて次へ（travel）。</summary>
    public TimeSpan TravelLimit { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>テレポの詠唱が始まらないとき、頼み直すまでの時間。</summary>
    public TimeSpan TeleportNoCast { get; init; } = TimeSpan.FromSeconds(10);

    public int TeleportAttempts { get; init; } = 3;

    /// <summary>vnavmesh の地図が作られもせず準備もできない（詰まっている）とき、読み込みを頼み直すまでの時間（vnavmesh の自作修正 WatchForStuckLoad と同じ考え）。</summary>
    public TimeSpan NavStuckAfter { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>最初の1回の後に、地図の読み込みを頼み直す回数。使い切っても準備できなければ、このエリアを諦めて次へ（mesh）。</summary>
    public int NavReloadLimit { get; init; } = 3;

    /// <summary>1体にかける時間の上限（届かない・倒しきれない個体を諦める）。</summary>
    public TimeSpan TargetLimit { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>諦めた個体をもう一度狙うまでの時間（こちらから仕掛ける相手だけ。攻撃してきた敵は諦めずに反撃する）。</summary>
    public TimeSpan GiveUpFor { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan SpawnWait { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>倒した後、所持数の変化を待つ時間（落とさないこともあるので、待つのはこの時間だけ）。</summary>
    public TimeSpan CountWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>同じ地点へ 3 回回ってきても攻撃してよい個体を見なければ諦める。0 なら諦めない。</summary>
    public int LapLimit { get; init; } = 3;

    /// <summary>反撃の相手が敵視リストから消えた（こちらを狙うのをやめた）まま、この時間たったら追うのをやめる。</summary>
    public TimeSpan RetaliationLostGrace { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>巡回の点の上に屋根・木・岩があるかを確かめる水平の距離（遠いと当たり判定が読み込まれていないことがあるので、近づいてから）。</summary>
    public float RoofCheckDistance { get; init; } = 150f;

    /// <summary>巡回の点の高さより、この値（m）以上の上に当たり判定の面があれば「屋根あり」とみなし、上空ではなく地面の点へ向かう。</summary>
    public float RoofMargin { get; init; } = 3f;

    /// <summary>索敵の範囲（自分から水平の距離）。飛んでいるときは広く見る。</summary>
    public float SearchRange { get; init; } = 60f;

    public float FlyingSearchRange { get; init; } = 100f;

    /// <summary>飛んで巡回するときの、巡回の点からの高さ（地面に下りて飛び直さない）。屋根のある点では足さない。</summary>
    public float PatrolAltitude { get; init; } = 10f;

    /// <summary>巡回の点に着いたとみなす水平の距離。</summary>
    public float SpotRange { get; init; } = 8f;

    /// <summary>敵の一覧を見て判定する間隔（毎フレームは重いので）。</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>条件待ちの理由（この順に調べる）。</summary>
public enum StandbyKind
{
    /// <summary>予定外のエリア移動（自分のテレポではない）。</summary>
    Zoning,

    /// <summary>自分のキャラクターを読めない（ログアウトそのものは Plugin の Logout のイベントで止める）。</summary>
    Unavailable,

    /// <summary>戦闘不能（自動では帰還・蘇生しない）。</summary>
    Dead,

    /// <summary>コンテンツ参加・リーヴ実行（この間は RSR を元の設定へ戻す）。</summary>
    Duty,

    /// <summary>戦闘ジョブでない・ジョブを読めない。</summary>
    Job,

    /// <summary>RotationSolverReborn が読み込まれていない。</summary>
    Rsr,
}

/// <summary>
/// 1品・1エリアの収集の状態機械。
///
///  ・こちらから仕掛けるのは、個体の判定（TargetJudge）を通り、この実行の狩り場（<see cref="FarmPlan.Areas"/>）に入る個体だけ（R1・D2）。
///  ・攻撃してきた敵（敵視リストにいる生きた敵）は、計画の対象かどうかに関係なく反撃して倒す。乗っていれば降りる（D1）。
///    危険でない理由で終わるときも、攻撃してきた敵を倒しきってから終える（FinishWhenClear）。
///    反撃の相手は諦めない（歩いて届かず乗れなければ、寄れる所まで寄って来るのを待つ。HP が減らない・時間がかかるときも離れない：KeepRetaliating）。
///    倒すまで、次の狙い・移動・乗る・テレポへ進まない。
///  ・RSR は開始の時点で控えを取り、Off（攻撃の抑止）にする。攻撃を許すのは Engage の間だけで、相手にハードターゲットを置いた後に
///    Henched を頼み、範囲攻撃 Off などと Henched を読み直してから（R3・R7）。移動・待機・所持数確認・条件待ちでは Off（R2・D4）。
///    RSR の設定が外から変わった・反映しない・読めないときは止めずに送り直す（RsrController。D10）。
///  ・危険を見つけても止めない（D10）：戦闘不能・コンテンツ・予定外のエリア移動などは条件待ち（<see cref="FarmState.Standby"/>）にし、
///    条件が消えたら準備からやり直す。ペット・チョコボは見ない。止まるのは利用者の停止・ログアウト・読み込みの解除・例外だけ（S1）。
///  ・見つからない・移動できない・地図が作れない・テレポできない・高さが決まらないときは、このエリアを諦めて止まる（GoalRunner が次へ進む：S3）。
///    同じ地点へ <see cref="FarmLimits.LapLimit"/> 回来ても見つからなければ諦める（D3）。
///  ・止めた移動は、止まったと確かめるまで控えを残して送り直す（R6）。確かめる前に別の経路を始めない。
///  ・進展は「選んだ品の所持数が増えた」ことだけで数える（倒しただけでは延ばさない）。その場の討伐だけは倒したことで数える。
/// </summary>
public sealed class FarmRunner
{
    /// <summary>Off のまま RSR の控えを保持して止める理由（利用者の「停止（Off のまま）」と、次のフレームが来ない・不具合のとき）。</summary>
    public static readonly IReadOnlySet<string> HoldCodes = new HashSet<string>
    {
        "logout", "unload", "exception", "user-hold",
    };

    private readonly IFarmWorld world;
    private readonly INavControl nav;
    private readonly RsrController rsr;
    private readonly ITravel travel;
    private readonly FoundSpotStore found;
    private readonly FarmLimits limits;

    private readonly Mover mover = new();
    private readonly Engagement engage = new();

    // 終わり方が決まっているが、攻撃してきた敵を倒してから終える（FinishWhenClear）。反撃の後に戻る状態（反撃が続く間だけ値を持つ）
    private (FarmState State, string Code, string Message, IReadOnlyList<string> Details)? pendingFinish;
    private FarmState? resumeState;

    // 「離れずに反撃を続けます」を記録した相手と時刻（同じ相手は 30 秒に 1 回だけ書く）
    private ulong keepLoggedFor;
    private DateTime keepLoggedAt = DateTime.MinValue;

    // 反撃中か・反撃の相手が敵視リストから消えた時刻
    private bool retaliating;
    private DateTime? retaliationLostSince;

    // 条件待ちの理由・RSR の控えを取り直した時刻・準備が長引いたことを記録した時刻
    private StandbyKind? standbyKind;
    private DateTime lastTakeTry = DateTime.MinValue;
    private DateTime armNoticeAt;

    // 地図の見張り：読み込みを頼んだ回数・作られもせず準備もできない状態が始まった時刻
    private int navReloads;
    private DateTime? navIdleSince;

    private FarmPlan? plan;
    private PatrolPlan? patrol;

    // 屋根の確認：確かめた巡回の点・屋根のある巡回の点（番号。巡回の計画を作るたびに空にする）
    private readonly HashSet<int> roofChecked = [];
    private readonly HashSet<int> roofed = [];
    private int spotIndex;
    private DateTime stateSince;
    private DateTime lastProgress;
    private int lastCount;
    private int startCount;
    private DateTime lastScan = DateTime.MinValue;

    // テレポ
    private bool teleportRequested;
    private DateTime teleportAt;
    private bool sawCast;
    private bool sawBetweenAreas;
    private int teleportAttempts;

    // 外したハードターゲットを記録した相手・オートアタックを止めた時刻
    private ulong foreignLogged;
    private readonly HashSet<ulong> rejectLogged = []; // 狙わない理由を記録に残した計画の敵（1 体につき 1 回）
    private DateTime lastAutoAttackToggle = DateTime.MinValue;

    // 狙っている個体（参照は持たず、ID と名前・基本 ID だけを持つ）
    private ulong targetId;
    private uint targetName;
    private uint targetBase;
    private DateTime targetSince;
    private readonly Dictionary<ulong, DateTime> giveUpAt = [];

    private int countAtKill;

    // 巡回の周回：この周で対象を見たか・対象を見ないまま回った周回数・点ごとの移動の失敗
    private bool seenThisLap;
    private int lapsWithout;
    private int lapStartIndex;
    private int spotFailures;

    public FarmRunner(IFarmWorld world, INavControl nav, RsrController rsr, ITravel travel, FoundSpotStore found, FarmLimits? limits = null)
    {
        this.world = world;
        this.nav = nav;
        this.rsr = rsr;
        this.travel = travel;
        this.found = found;
        this.limits = limits ?? new FarmLimits();
    }

    /// <summary>こちらから仕掛けてよいかの判定（個体の見分け＋この実行の狩り場）。</summary>
    private Judgement Judge(MobSnapshot m, PlayerContext me) => TargetJudge.Judge(m, me, this.plan!.Areas);

    /// <summary>
    /// 計画のエリアの狩り場の点のうち、高さが分からない（NaN：カタログの点）ものに床の高さを入れる（地図待ちで1回）。
    /// 決まらない点は巡回に使わない（記録に残す）。狩り場の範囲（Anchor の X・Z）は変えない。
    /// </summary>
    private List<HuntArea> ResolveHeights(FarmPlan p)
    {
        var result = new List<HuntArea>();
        foreach (var area in p.Areas.Where(a => a.Territory == p.Territory))
        {
            var pts = new List<AreaPoint>();
            foreach (var pt in area.Points)
            {
                if (!float.IsFinite(pt.X) || !float.IsFinite(pt.Z))
                    continue;
                if (float.IsFinite(pt.Y))
                {
                    pts.Add(pt);
                    continue;
                }

                if (this.FloorY(pt.X, pt.Z) is { } y)
                    pts.Add(pt with { Y = y });
                else
                    Core.DebugLog.Current?.Line("探索", $"{area.NameId} の点 ({pt.X:0},{pt.Z:0}) の床が、地図にも当たり判定にも見つかりません。巡回に使いません");
            }

            result.Add(area with { Points = pts });
        }

        return result;
    }

    /// <summary>
    /// その水平位置の床の高さ。vnavmesh の床（本来の地面とつながった床 → つながっていない床。どちらも上から一番高い床）を先に見て、
    /// 地図に床が無ければゲームの当たり判定（真上から真下へのレイキャスト）を使う。遠くて当たり判定が読み込まれていなければ null。
    /// </summary>
    private float? FloorY(float x, float z)
    {
        var top = new Vector3(x, 1024f, z);
        if (this.nav.PointOnFloor(top, false, 5f) is { } a)
            return a.Y;
        if (this.nav.PointOnFloor(top, true, 5f) is { } b)
            return b.Y;
        if (this.world.GroundBelow(x, z) is { } g)
            return this.nav.NearestPointReachable(new Vector3(x, g, z), 5f, 10f)?.Y ?? g;
        return null;
    }

    public FarmState State { get; private set; } = FarmState.Idle;

    /// <summary>いまの状態の説明（画面用）。</summary>
    public string Status { get; private set; } = "待機";

    /// <summary>止まった理由（無ければ null）。</summary>
    public StopInfo? Stop { get; private set; }

    public FarmPlan? Plan => this.plan;

    public PatrolPlan? Patrol => this.patrol;

    public int SpotIndex => this.spotIndex;

    /// <summary>倒した数（反撃で倒した数を含む）。</summary>
    public int Kills { get; private set; }

    public int StartCount => this.startCount;

    public int CurrentCount => this.lastCount;

    /// <summary>いま狙っている個体（無ければ 0）。</summary>
    public ulong TargetId => this.targetId;

    /// <summary>いま狙っている個体の、狙ったときの名前 ID（画面・試験用）。</summary>
    public uint TargetNameId => this.targetId == 0 ? 0 : this.targetName;

    /// <summary>攻撃してきた敵に反撃しているか（画面用）。</summary>
    public bool Retaliating => this.retaliating;

    /// <summary>反撃している相手の名前（反撃していなければ null。画面用）。</summary>
    public string? RetaliationTargetName => this.retaliating ? this.world.FindMob(this.targetId)?.Name : null;

    /// <summary>対象を一度も見ないまま回った周回数（画面・試験用）。</summary>
    public int LapsWithoutFind => this.lapsWithout;

    /// <summary>この周回数で諦める（0 なら諦めない）。</summary>
    public int LapLimit => this.limits.LapLimit;

    /// <summary>倒した個体（記録用。倒したと判断した時点の写し）。</summary>
    public event Action<MobSnapshot>? Killed;

    /// <summary>動いているか（待機・完了・停止でない。条件待ちは動いている扱い）。</summary>
    public bool Running => this.State is not (FarmState.Idle or FarmState.Done or FarmState.Stopped);

    /// <summary>止めた自分の移動を、まだ止まったと確かめていないか（確かめるまで新しい移動を始めない）。</summary>
    public bool MoveStopPending => this.mover.StopPending || this.engage.StopPending;

    /// <summary>
    /// 戦う段か（狙い・反撃の相手のそばで降りて、ハードターゲットを置いた後）。BMR に足を任せるのはこの間だけ（BmrAssist）。
    /// </summary>
    public bool InAttackPhase => this.State == FarmState.Engage && this.engage.AttackPhase;

    /// <summary>コンテンツ参加中・リーヴ実行中の条件待ちか（この間は RSR・BMR を利用者や他のプラグインへ返す）。</summary>
    public bool InDutyStandby => this.State == FarmState.Standby && this.standbyKind == StandbyKind.Duty;

    /// <summary>
    /// 戦う段で、こちらの近づく移動を控えるか（BMR が動かしている間。Plugin が BmrAssist.YieldMovement をつなぐ。null なら控えない）。
    /// </summary>
    public Func<bool>? YieldMovement { get; set; }

    /// <summary>移動を止めたと確かめられない理由（画面用。無ければ null）。</summary>
    public string? MoveStopProblem => this.mover.StopProblem ?? this.engage.StopProblem;

    /// <summary>状態の表示名。</summary>
    public string StateName => this.State switch
    {
        FarmState.Idle => "待機",
        FarmState.Arm => "事前確認（戦闘連携の準備）",
        FarmState.Travel => "エリア移動",
        FarmState.NavWait => "エリア移動（地図の準備待ち）",
        FarmState.ToSpot => "狩り場へ移動",
        FarmState.Search => "索敵・対象判定",
        FarmState.Engage => this.retaliating ? "反撃" : this.engage.InReachNow ? "討伐" : "接近",
        FarmState.CountCheck => "所持数確認",
        FarmState.SpawnWait => "再出現待ち",
        FarmState.Standby => "条件待ち（自動で再開）",
        FarmState.Done => "完了",
        FarmState.Stopped => "停止",
        _ => this.State.ToString(),
    };

    // ------------------------------------------------------------------
    // 開始・条件待ち・停止
    // ------------------------------------------------------------------

    /// <summary>
    /// 始める前の点検。始められない理由の一覧（空なら始められる）。待っても直らない物だけを見る。
    /// 戦闘不能・コンテンツ・ジョブ・自分を読めない・前の移動の停止待ちなどは、始めた後に条件待ちや準備で扱う。
    /// </summary>
    public List<string> Precheck(FarmPlan p)
    {
        var problems = new List<string>();
        var me = this.world.Player;
        if (!p.AdHoc && p.TargetCount <= 0)
            problems.Add("目標の所持数が 0 以下です");
        if (!this.rsr.IsRsrLoaded)
            problems.Add("RotationSolverReborn が読み込まれていません");
        if (!this.nav.IsLoaded)
            problems.Add("vnavmesh が読み込まれていません");
        else if (this.nav.IsAutoLoad() == false)
            problems.Add("vnavmesh の「地図を自動で読み込む」（AutoLoadNavmesh）が OFF です。OFF だとエリア移動の後に地図が空になり移動できません。vnavmesh の設定で ON にしてください");
        if (!p.Areas.Any(a => a.Territory == p.Territory && a.Points.Any(pt => pt.Anchor)))
            problems.Add("このエリアに、カタログの地点がありません");
        if (me.Available && me.Territory != p.Territory && !this.travel.Available)
            problems.Add("エリア移動には Lifestream が必要です");
        else if (me.Available && me.Territory != p.Territory && this.travel.FindAetheryte(p.Territory, null) == null)
            problems.Add("狩り場のエリアに解放済みのエーテライトがありません");
        if (!p.AdHoc && this.world.HasRoomFor(p.ItemId) == false)
            problems.Add($"所持品に {p.ItemName} が入る余地がありません");
        return problems;
    }

    /// <summary>
    /// その場の討伐を始める（座標のクリック：D6）。狩り場はクリックした地点1つ。
    /// 走っている・複数品の品と品のあいだ（RSR を持ち続けている）・移動の停止を確かめている間は false（割り込まない）。
    /// </summary>
    public bool StartAdHoc(string label, HuntArea area)
        => !this.Running && !this.KeepRsrOnFinish && !this.MoveStopPending
            && this.Start(new FarmPlan(0, label, 0, area.Territory, [area], AdHoc: true));

    /// <summary>始める。始められなければ停止の理由を残して false。既に目標以上なら何も動かさずに完了（true）。</summary>
    public bool Start(FarmPlan p)
    {
        if (this.Running)
            return false;
        this.ResetRun();
        this.plan = p;
        var now = this.world.Now;

        // 既に目標以上なら、何も動かさずに完了（読めなければ、読めるまで CheckCount が待つ）
        if (!p.AdHoc && this.world.CountItem(p.ItemId) is { } c)
        {
            this.startCount = c;
            this.lastCount = c;
            if (c >= p.TargetCount)
            {
                this.Finish(FarmState.Done, "already", $"既に {p.ItemName} を {c} 個持っています（目標 {p.TargetCount}）", []);
                return true;
            }
        }

        var problems = this.Precheck(p);
        if (problems.Count > 0)
        {
            this.Finish(FarmState.Stopped, "precheck", "始められません：" + problems[0], problems);
            return false;
        }

        // RSR の控えを取り、すぐ Off にする（同じ呼び出しの中で送る＝次のフレームまで元の Auto 等のまま動かさない）。
        // その後で、対象外のハードターゲットとオートアタックを外す（R3）。控えを取れなければ、準備（TickArm）が取り直す
        if (!this.rsr.Controlling)
            this.rsr.Take(now);
        if (this.rsr.Controlling)
        {
            this.rsr.Suppress();
            this.rsr.Tick(now);
            this.ClearForeignTarget(this.world.Player, now);
        }

        this.EnsureAutoAttackOff(now);
        this.lastProgress = now;
        this.Enter(FarmState.Arm, "RSR を Off にし、範囲攻撃 Off・勝手に狙う設定 false を読み直して確かめています");
        Core.DebugLog.Current?.Line("収集", p.AdHoc ? $"その場の討伐を始めます：{p.ItemName}" : $"開始：{p.ItemName} を {p.TargetCount} 個まで・エリア {p.Territory}");
        return true;
    }

    /// <summary>
    /// 止める（利用者の操作）。自分の移動と狙いを解く。<paramref name="restoreRsr"/> が true なら RSR を元の設定へ戻し（読み直して確かめる。
    /// 元が Auto なら RSR が近くの敵を自分で選んで攻撃し始める）、false なら Off のまま控えを保持する（/amf stop と同じ）。攻撃してきた敵がいても待たない。
    /// </summary>
    public void StopNow(string reason, bool restoreRsr = true)
    {
        if (this.State is FarmState.Idle or FarmState.Done or FarmState.Stopped)
            return;
        this.Finish(FarmState.Stopped, restoreRsr ? "user" : "user-hold", reason, []);
    }

    /// <summary>
    /// 集めている品の目標数を変える（自動討伐の画面で収集中に数を変えたとき）。
    /// 下げて所持数が届いていれば、次のフレームの所持数の確かめ（CheckCount）で終える（攻撃してきた敵がいれば倒してから）。
    /// 上げたときに、目標に達したので攻撃してきた敵を倒してから終えようとしていたら、終えるのをやめて集め続ける。
    /// その場の討伐（品の目標なし）・走っていないときは何もしない。
    /// </summary>
    public void SetTarget(int target)
    {
        if (this.plan is not { AdHoc: false } p || !this.Running || p.TargetCount == target)
            return;
        this.plan = p with { TargetCount = target };
        Core.DebugLog.Current?.Line("収集", $"{p.ItemName} の目標数を {p.TargetCount} → {target} に変えました（いま {this.lastCount} 個）");
        if (this.pendingFinish is { } pf && pf.Code == "target" && this.lastCount < target)
        {
            this.pendingFinish = null;
            Core.DebugLog.Current?.Line("収集", "目標数を上げたので、終えるのをやめて集め続けます（攻撃してきた敵は今までどおり倒します）");
        }
    }

    /// <summary>
    /// 終わった（完了・危険でない理由の停止）ときも RSR を Off のまま控えを持ち続けるか（複数品を続けて集める層が使う）。
    /// 利用者の「停止（元に戻す）」と保持の理由には効かない。最後は <see cref="ReleaseRsr"/> で戻すか保持する。
    /// </summary>
    public bool KeepRsrOnFinish { get; set; }

    /// <summary>
    /// 走っていないときに、持ち続けていた RSR を元の設定へ戻す（<paramref name="restore"/>）か、Off のまま保持する。
    /// 走っている（条件待ちを含む）ときは何もしない（その場合は <see cref="StopNow"/> を使う）。
    /// </summary>
    public void ReleaseRsr(bool restore, string why)
    {
        if (this.Running || !this.rsr.Controlling)
            return;
        if (restore)
            this.rsr.Restore(this.world.Now);
        else
            this.rsr.Hold(why);
    }

    /// <summary>Off のまま RSR の控えを保持して止める（読み込みの解除・ログアウト・例外。<paramref name="code"/> は <see cref="HoldCodes"/> のどれか）。</summary>
    public void HoldStop(string code, string reason)
    {
        if (this.State is FarmState.Idle or FarmState.Done or FarmState.Stopped)
        {
            // 次の品を待つ間に持ち続けていた RSR も、Off のまま保持する（読み込みの解除の後は誰も戻せないので、黙って捨てない）
            this.ReleaseRsr(false, reason);
            return;
        }

        this.Finish(FarmState.Stopped, code, reason, []);
    }

    // ------------------------------------------------------------------
    // 毎フレーム
    // ------------------------------------------------------------------

    /// <summary>1フレーム進める（Plugin.OnUpdate から。RsrController.Tick より先に呼ぶ）。</summary>
    public void Tick()
    {
        // 止めた自分の移動が止まったかを、動いていない間も確かめ続ける（R6）
        this.mover.TickStop(this.nav);
        this.engage.TickStop(this.nav);
        if (!this.Running && this.rsr.HeldButHenched)
            this.ClearCombatTarget("止めた後ですが RSR が Henched のまま（Off の命令が効かない）なので");
        if (this.plan is not { } p || !this.Running)
            return;

        var now = this.world.Now;
        var me = this.world.Player;

        // ① 条件待ち（D10・S2）：理由があるあいだは何もしない。理由が消えたら準備からやり直す
        if (this.WaitReason(me) is { } wait)
        {
            this.Standby(wait.Kind, wait.Text, now);
            return;
        }

        if (this.State == FarmState.Standby)
        {
            this.LeaveStandby(now);
            return;
        }

        // ② 予定外のエリア：止めずに準備からやり直す（準備がテレポで戻す）。反撃中でテレポ前（戻り先が準備）のときは除く（§4.6）
        if (this.State is not (FarmState.Arm or FarmState.Travel)
            && !(this.retaliating && this.resumeState == FarmState.Arm)
            && me.Territory != p.Territory)
        {
            this.Recover($"予定外のエリアにいます（いま {me.Territory} / 狩り場 {p.Territory}）。狩り場へ戻ります");
            return;
        }

        // ③ 終わり方が決まっていて（§4.6-8）、攻撃してくる敵がもういない → 終える
        if (this.pendingFinish is { } pf && this.State != FarmState.Engage && this.Attackers(now).Count == 0)
        {
            this.pendingFinish = null;
            this.resumeState = null;
            this.Finish(pf.State, pf.Code, pf.Message, pf.Details);
            return;
        }

        // ④ 所持数（目標・満杯）。読めない間は待つ。その場の討伐は品が無いので見ない
        if (!this.world.BetweenAreas && !p.AdHoc && this.CheckCount(p, now))
            return;

        // ⑤ 進展なし（倒しても所持数が増えない。条件待ちの時間は数えない：LeaveStandby で数え直す）
        if (this.pendingFinish == null && now - this.lastProgress > this.limits.NoProgress)
        {
            this.FinishWhenClear(FarmState.Stopped, "noprogress", p.AdHoc
                ? $"{this.limits.NoProgress.TotalMinutes:0}分間、対象を倒していません（長時間の進展なし）"
                : $"{this.limits.NoProgress.TotalMinutes:0}分間、{p.ItemName} の所持数が増えていません（長時間の進展なし。倒しただけでは延ばしません）", []);
            return;
        }

        // ⑥ 反撃の割り込み（D1・§4.6）
        if (!this.world.BetweenAreas && this.TryRetaliate(now))
            return;

        // ⑦ 自分の対象・攻撃してきた敵以外へのハードターゲットを外す（R3）
        this.ClearForeignTarget(me, now);

        if (this.State != FarmState.Engage)
        {
            this.rsr.Suppress();
            this.EnsureAutoAttackOff(now);
        }

        switch (this.State)
        {
            case FarmState.Arm:
                this.TickArm(p, now, me);
                break;
            case FarmState.Travel:
                this.TickTravel(p, now, me);
                break;
            case FarmState.NavWait:
                this.TickNavWait(p, now, me);
                break;
            case FarmState.ToSpot:
                this.TickToSpot(now, me);
                break;
            case FarmState.Search:
                this.TickSearch(now, me);
                break;
            case FarmState.SpawnWait:
                this.TickSpawnWait(now, me);
                break;
            case FarmState.Engage:
                this.TickEngage(now, me);
                break;
            case FarmState.CountCheck:
                this.TickCountCheck(now, me);
                break;
        }
    }

    /// <summary>条件待ちの理由（無ければ null）。自分のテレポ（Travel）の間のエリア移動は理由にしない。</summary>
    private (StandbyKind Kind, string Text)? WaitReason(PlayerContext me)
    {
        if (this.world.BetweenAreas)
            return this.State == FarmState.Travel ? null : (StandbyKind.Zoning, "エリア移動中です（自分のテレポではありません）。終わったら狩り場へ戻ります");
        if (!me.Available)
            return (StandbyKind.Unavailable, "自分のキャラクターを読めるのを待っています");
        if (this.world.PlayerDead)
            return (StandbyKind.Dead, "戦闘不能です。復帰したら狩り場へ戻って続けます（自動では復帰しません）");
        if (me.BoundByDuty || me.Directors.Any(IdRules.LeveDirectors.Contains))
            return (StandbyKind.Duty, "コンテンツ参加中・リーヴ実行中です。終わったら狩り場へ戻って続けます（その間 RSR は元の設定に戻しています）");
        if (this.world.CombatJob != true)
            return (StandbyKind.Job, this.world.CombatJob == null ? "いまのジョブを読めません。読めたら続けます" : $"いまのジョブ（{this.world.JobName}）は戦闘ジョブではありません。戦闘ジョブに替えると続けます");
        if (!this.rsr.IsRsrLoaded)
            return (StandbyKind.Rsr, "RotationSolverReborn が読み込まれていません。読み込まれたら続けます");
        return null;
    }

    /// <summary>
    /// 条件待ちに入る・続ける。入ったとき（理由の種類が変わったとき）だけ後片づけをする。コンテンツ・リーヴの間は RSR を元の設定へ戻し、
    /// それ以外は Off にする（コンテンツで手放した後に別の条件待ちへ移ったら、控えを取り直して Off に戻す）。この間は反撃しない。
    /// </summary>
    private void Standby(StandbyKind kind, string text, DateTime now)
    {
        if (this.State != FarmState.Standby || this.standbyKind != kind)
        {
            this.Halt();                         // 自分の移動・近づく移動・狙い・テレポの頼み・オートアタック（今の Halt）
            this.retaliating = false;
            this.retaliationLostSince = null;
            this.resumeState = null;             // 終わったら準備からやり直す（pendingFinish は残す：§4.5-2 の③で終える）
            this.standbyKind = kind;
            if (kind == StandbyKind.Duty && this.rsr.Controlling)
                this.rsr.Restore(now);           // S2：コンテンツでは利用者・他のプラグインが RSR を使う
            this.Enter(FarmState.Standby, text);
            Core.DebugLog.Current?.Line("収集", $"条件待ち：{text}");
        }
        else
        {
            this.Status = text;
        }

        if (kind != StandbyKind.Duty)
        {
            // コンテンツで手放した後に、別の条件待ちへ移った場合も Off へ戻す。
            if (!this.rsr.Controlling && now - this.lastTakeTry >= TimeSpan.FromSeconds(1))
            {
                this.lastTakeTry = now;
                this.rsr.Take(now);
            }

            this.rsr.Suppress();
        }
    }

    /// <summary>条件待ちが終わった。待った時間を「進展なし」に数えず、準備（RSR の確かめ）からやり直す。</summary>
    private void LeaveStandby(DateTime now)
    {
        var was = this.standbyKind;
        this.standbyKind = null;
        this.lastProgress = now;   // 待った時間を「進展なし」に数えない
        this.Enter(FarmState.Arm, "条件待ちが終わりました。RSR の状態を確かめてから狩り場へ戻ります");
        Core.DebugLog.Current?.Line("収集", $"条件待ち（{was}）が終わりました。準備からやり直します");
    }

    /// <summary>止めずに準備からやり直す（予定外のエリアなど）。</summary>
    private void Recover(string message)
    {
        this.Halt();
        this.retaliating = false;
        this.retaliationLostSince = null;
        this.resumeState = null;
        this.Enter(FarmState.Arm, message);
        Core.DebugLog.Current?.Line("収集", $"やり直し：{message}");
    }

    /// <summary>敵へのハードターゲットを外す（止めた後に RSR が Henched のままだと読めるときだけ使う）。</summary>
    private void ClearCombatTarget(string why)
    {
        var hid = this.world.HardTargetId;
        if (hid == 0 || this.world.FindMob(hid) is not { IsCombatant: true } m)
            return;
        this.world.SetHardTarget(null);
        if (this.foreignLogged != hid)
        {
            this.foreignLogged = hid;
            Core.DebugLog.Current?.Line("収集", $"{why}、{m.Name} へのハードターゲットを外しました");
        }
    }

    /// <summary>
    /// 自分の対象でも攻撃してきた敵でもない敵にハードターゲットが置かれていて、その敵が仕掛けてよい個体でなければ外す（R3）。
    /// 敵でないもの（NPC・プレイヤー）には触らない。攻撃してきた敵は外さない（反撃で倒す相手。他者が選んでも「置き換え」と数えない）。
    /// 戦っている最中に置き換えられたら 1 秒だけ攻撃を抑止する（回数を数えて止めることはしない：D10）。
    /// </summary>
    private void ClearForeignTarget(PlayerContext me, DateTime now)
    {
        if (!this.rsr.Controlling)
            return;
        var id = this.world.HardTargetId;
        if (id == 0 || id == this.targetId || this.world.FindMob(id) is not { IsCombatant: true } mob)
            return;
        if (this.Attackers(now).Any(a => a.GameObjectId == id))
            return;
        var j = this.Judge(mob, me);
        if (j.Attackable && this.State != FarmState.Engage)
            return;
        this.world.SetHardTarget(null);
        if (this.foreignLogged != id)
        {
            this.foreignLogged = id;
            Core.DebugLog.Current?.Line("収集", $"自分の対象でない {mob.Name}（{TargetJudge.Name(j.Verdict)}）へのハードターゲットを外しました");
        }

        // 戦っている最中に他者がターゲットを置いた（RSR が Henched かもしれない）。しばらく攻撃を抑止し、オートアタックも止める
        if (this.State == FarmState.Engage)
        {
            this.contestedUntil = now + ContestedSuppress;
            this.rsr.Suppress();
            this.EnsureAutoAttackOff(now);
        }
    }

    /// <summary>他者のハードターゲットを外した後、攻撃を抑止する時間。</summary>
    public static readonly TimeSpan ContestedSuppress = TimeSpan.FromSeconds(1);

    private DateTime contestedUntil = DateTime.MinValue;

    /// <summary>オートアタックが動いていれば止める（トグルなので、動いていると読めたときだけ。0.5 秒あける）。</summary>
    private void EnsureAutoAttackOff(DateTime now)
    {
        if (this.world.AutoAttacking == true && now - this.lastAutoAttackToggle >= TimeSpan.FromMilliseconds(500))
        {
            this.lastAutoAttackToggle = now;
            this.world.ToggleAutoAttack();
            Core.DebugLog.Current?.Line("収集", "オートアタックが動いていたので止めました（攻撃の抑止）");
        }
    }

    /// <summary>
    /// 所持数を見る（目標に達した・満杯）。終える（終わりを決めた）なら true。読めない間は待つ（止めない：D10）。
    /// 進展は所持数の増加だけで数える。終わり方が決まった後（攻撃してきた敵を倒している間）は数え直すだけ。
    /// </summary>
    private bool CheckCount(FarmPlan p, DateTime now)
    {
        if (this.world.CountItem(p.ItemId) is not { } c)
            return false;
        if (c > this.lastCount)
            this.lastProgress = now;
        this.lastCount = c;
        if (this.pendingFinish != null)
            return false;
        if (c >= p.TargetCount)
        {
            // 倒した瞬間に所持数が先に増えることがある（同じフレーム）。倒れている狙いの敵は倒した数に入れてから終える
            if (this.State == FarmState.Engage && this.world.FindMob(this.targetId) is { } m && m.NameId == this.targetName && m.BaseId == this.targetBase && (m.Dead || m.Hp == 0))
            {
                this.Kills++;
                this.Killed?.Invoke(m);
                this.DropTarget(now, giveUp: false);
            }

            this.FinishWhenClear(FarmState.Done, "target", $"{p.ItemName} が {c} 個になりました（目標 {p.TargetCount}・倒した数 {this.Kills}）", []);
            return true;
        }

        if (this.world.HasRoomFor(p.ItemId) == false)
        {
            this.FinishWhenClear(FarmState.Stopped, "full", $"所持品に {p.ItemName} が入る余地がありません", []);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 準備：RSR の控えを取り（取れなければ 1 秒おきに取り直す）、攻撃の抑止（Off）・範囲攻撃 Off・勝手に狙う設定 false・外部ターゲット指定なし・
    /// オートアタック停止・前の移動の停止を、すべて読み直して確かめてから動く（R7）。時間では止めない（ArmNotice ごとに足りない物を記録に書く）。
    /// </summary>
    private void TickArm(FarmPlan p, DateTime now, PlayerContext me)
    {
        if (!this.rsr.Controlling)
        {
            if (now - this.lastTakeTry >= TimeSpan.FromSeconds(1))
            {
                this.lastTakeTry = now;
                this.rsr.Take(now);
            }

            if (!this.rsr.Controlling)
            {
                this.Status = $"RSR の今の設定を読めるのを待っています（{this.rsr.Problem}）";
                return;
            }
        }

        // 攻撃の抑止（Off）・範囲攻撃 Off・勝手に狙う設定 false・外部ターゲット指定なし・オートアタック停止を、すべて読み直して確かめてから動く（R7）
        if (this.rsr.SuppressedVerified && this.rsr.SettingsVerified && this.world.AutoAttacking == false && !this.MoveStopPending)
        {
            if (me.Territory != p.Territory)
            {
                this.teleportRequested = false;
                this.teleportAttempts = 0;
                this.Enter(FarmState.Travel, "狩り場のエリアへテレポします");
            }
            else
            {
                this.Enter(FarmState.NavWait, "vnavmesh の地図を確かめています");
            }

            return;
        }

        this.Status = this.MoveStopPending
            ? this.MoveStopProblem ?? "前の移動が止まったかを確かめています"
            : "RSR を Off にし、範囲攻撃 Off・勝手に狙う設定 false を読み直して確かめています";
        if (now - this.armNoticeAt >= this.limits.ArmNotice)
        {
            var missing = new List<string>();
            if (!this.rsr.SuppressedVerified)
                missing.Add("モード Off");
            if (!this.rsr.SettingsVerified)
                missing.Add("範囲攻撃 Off・TargetFreely false・IgnoreNonFateInFate false・外部ターゲット指定なし");
            if (this.world.AutoAttacking != false)
                missing.Add(this.world.AutoAttacking == null ? "オートアタックの状態（読めない）" : "オートアタックの停止");
            if (this.MoveStopPending)
                missing.Add("前の移動の停止");
            this.armNoticeAt = now;
            Core.DebugLog.Current?.Line("収集", $"準備を待っています：{string.Join("・", missing)}（止めずに待ち続けます）");
        }
    }

    /// <summary>
    /// テレポ（Lifestream）。詠唱とエリア移動を見て、途切れたら頼み直す。移れないまま期限・回数を使い切ったら、このエリアを諦めて次へ（travel）。
    /// </summary>
    private void TickTravel(FarmPlan p, DateTime now, PlayerContext me)
    {
        if (!this.world.BetweenAreas && me.Available && me.Territory == p.Territory && this.world.PlayerFree)
        {
            this.Enter(FarmState.NavWait, "着きました。vnavmesh の地図を確かめています");
            return;
        }

        if (now - this.stateSince > this.limits.TravelLimit)
        {
            this.FinishWhenClear(FarmState.Stopped, "travel", $"{this.limits.TravelLimit.TotalMinutes:0}分以内に狩り場のエリアへ移動できませんでした", []);
            return;
        }

        if (this.teleportRequested)
        {
            var casting = this.travel.CastingTeleport;
            this.sawCast |= casting;
            this.sawBetweenAreas |= this.world.BetweenAreas;
            if (casting || this.world.BetweenAreas || this.sawBetweenAreas)
            {
                this.Status = this.world.BetweenAreas ? "エリア移動中" : "テレポの詠唱中";
                return;
            }

            // 詠唱を見た後に途切れた、または詠唱が始まらない → 頼み直す
            if (!this.sawCast && now - this.teleportAt < this.limits.TeleportNoCast)
            {
                this.Status = "テレポの詠唱が始まるのを待っています";
                return;
            }

            Core.DebugLog.Current?.Line("テレポ", this.sawCast ? "テレポの詠唱が途切れました。頼み直します" : "テレポの詠唱が始まりません。頼み直します");
            this.teleportRequested = false;
        }

        // Lifestream が断る条件（動作の硬直・自分をターゲットできない）も頼む前に確かめる（断られて回数を使い切らない）
        if (this.world.InCombat || !this.world.PlayerFree || this.travel.TeleportStatus != 0 || !this.travel.ReadyToTeleport)
        {
            this.Status = this.world.InCombat ? "戦闘中なのでテレポを待っています" : "テレポが使えるようになるのを待っています";
            return;
        }

        // 断られたときに毎フレーム頼み直さない（1秒あける）
        if (now - this.teleportAt < TimeSpan.FromSeconds(1))
            return;

        if (this.teleportAttempts >= this.limits.TeleportAttempts)
        {
            this.FinishWhenClear(FarmState.Stopped, "travel", $"テレポを {this.limits.TeleportAttempts} 回頼んでも狩り場のエリアへ移れませんでした", []);
            return;
        }

        // テレポ前のカタログには高さがない。エーテライトの選択には水平位置だけを渡す。
        var first = p.Areas.Where(a => a.Territory == p.Territory).SelectMany(a => a.Points)
            .Where(pt => pt.Anchor && float.IsFinite(pt.X) && float.IsFinite(pt.Z))
            .Select(pt => (Vector3?)new Vector3(pt.X, 0, pt.Z)).FirstOrDefault();
        if (this.travel.FindAetheryte(p.Territory, first) is not { } aetheryte)
        {
            this.FinishWhenClear(FarmState.Stopped, "travel", "狩り場のエリアに解放済みのエーテライトがありません", []);
            return;
        }

        this.teleportAt = now;
        if (!this.travel.Teleport(aetheryte, out var why))
        {
            // 受け付けられなかった回は数えない（全体の時間の上限で止まる）
            this.Status = $"テレポを受け付けませんでした（{why}）。やり直します";
            Core.DebugLog.Current?.Line("テレポ", this.Status);
            return;
        }

        this.teleportAttempts++;
        this.teleportRequested = true;
        this.teleportAt = now;
        this.sawCast = false;
        this.sawBetweenAreas = false;
        this.Status = "テレポを頼みました";
    }

    /// <summary>
    /// vnavmesh の地図の準備待ち。準備ができたら狩り場の点の高さを決めて巡回の計画を作る（点が残らなければ height で次へ）。
    /// 作っている間は時間で諦めない。作られもせず準備もできない（詰まっている）なら、すぐ1回・その後 NavStuckAfter ごとに NavReloadLimit 回
    /// 読み込みを頼み直し、それでも駄目ならこのエリアを諦めて次へ（mesh）。
    /// </summary>
    private void TickNavWait(FarmPlan p, DateTime now, PlayerContext me)
    {
        if (this.nav.IsReady())
        {
            this.navReloads = 0;
            this.navIdleSince = null;
            this.patrol = PatrolPlan.Build(this.ResolveHeights(p), me.Position);
            this.roofChecked.Clear();
            this.roofed.Clear();
            if (this.patrol.Spots.Count == 0)
            {
                this.FinishWhenClear(FarmState.Stopped, "height", "狩り場の点の高さ（床）が、地図にも当たり判定にも見つかりません（このエリアは諦めて次へ）", []);
                return;
            }

            Core.DebugLog.Current?.Line("収集", $"巡回の点 {this.patrol.Spots.Count} か所を "
                + string.Join("→", this.patrol.Spots.Select(s => $"({s.Center.X:0},{s.Center.Y:0},{s.Center.Z:0})")) + " の順に回ります");
            this.spotIndex = 0;
            this.lapStartIndex = 0;
            this.seenThisLap = false;
            this.lapsWithout = 0;
            this.GoToSpot(now);
            return;
        }

        var progress = this.nav.BuildProgress();
        if (progress is >= 0 and < 1)
        {
            this.navIdleSince = null;   // 作っている（進みがある）間は待つ。時間の上限は無い（全体の NoProgress が見る）
            this.Status = $"vnavmesh の地図を作っています（{progress * 100:0}%）";
            return;
        }

        // 作っていない・準備もできていない＝詰まっている（vnavmesh の WatchForStuckLoad と同じ考え：§2.2）
        this.navIdleSince ??= now;
        if (this.navReloads == 0 || now - this.navIdleSince.Value >= this.limits.NavStuckAfter)
        {
            if (this.navReloads >= 1 + this.limits.NavReloadLimit)
            {
                this.FinishWhenClear(FarmState.Stopped, "mesh", $"vnavmesh の地図が、読み込みを {this.navReloads} 回頼んでも準備できません（このエリアは諦めて次へ）"
                    + (this.nav.IsAutoLoad() == false ? "（vnavmesh の自動読み込みが OFF です）" : string.Empty), []);
                return;
            }

            this.navReloads++;
            this.navIdleSince = now;
            this.nav.Reload();
            Core.DebugLog.Current?.Line("探索", $"vnavmesh に地図の読み込みを頼みました（{this.navReloads} 回目）");
        }

        this.Status = $"vnavmesh の地図の準備を待っています（読み込みを頼んだ回数 {this.navReloads}）";
    }

    /// <summary>今の巡回の点へ向かう（飛べるなら上空。屋根のある点は地面）。</summary>
    private void GoToSpot(DateTime now)
    {
        var spot = this.patrol!.Spots[this.spotIndex];
        var dest = spot.Center;
        Vector3? walkTo = null;
        if (this.world.CanFlyHere && !this.roofed.Contains(this.spotIndex))
        {
            dest += new Vector3(0, this.limits.PatrolAltitude, 0); // 飛べるなら巡回の点の少し上を通り、上から探す
            walkTo = spot.Center; // 近くて歩くときは地面の点へ（空中の点へは地上の経路が引けない）
        }
        this.mover.Start(this.world, dest, this.limits.SpotRange, $"巡回の点 {this.spotIndex + 1}/{this.patrol.Spots.Count}", walkTo: walkTo);
        this.Enter(FarmState.ToSpot, this.mover.Status);
    }

    /// <summary>
    /// 次の巡回の点へ。全部の点を1周したら（点が1つなら再出現待ちが1回終わるたびに）周回を数え、対象を一度も見ないまま
    /// LapLimit 周したら諦める（nofind：D3）。対象を見た周があれば数え直す。
    /// </summary>
    private void NextSpot(DateTime now)
    {
        var count = this.patrol!.Spots.Count;
        this.spotIndex = (this.spotIndex + 1) % count;
        if (this.spotIndex == this.lapStartIndex)
        {
            // 一周した
            if (!this.seenThisLap)
                this.lapsWithout++;
            else
                this.lapsWithout = 0;
            this.seenThisLap = false;
            this.spotFailures = 0;
            if (this.limits.LapLimit > 0 && this.lapsWithout >= this.limits.LapLimit)
            {
                this.FinishWhenClear(FarmState.Stopped, "nofind", $"巡回の点を {this.lapsWithout} 周しても、攻撃してよい個体が見つかりません", []);
                return;
            }

            if (this.lapsWithout > 0)
                Core.DebugLog.Current?.Line("収集", $"見つからないまま {this.lapsWithout}/{this.limits.LapLimit} 周");
        }

        this.GoToSpot(now);
    }

    /// <summary>巡回の点へ移動。移動しながら探し（見つければ着く前に狙う）、近づいたら屋根を確かめる。全部の点へ行けなければ move で次へ。</summary>
    private void TickToSpot(DateTime now, PlayerContext me)
    {
        if (this.TryPickTarget(now, me))
            return;

        this.CheckRoof();
        switch (this.mover.Tick(this.world, this.nav, ignoreHeight: this.world.CanFlyHere && !this.roofed.Contains(this.spotIndex)))
        {
            case Mover.Result.Arrived:
                // 着いた。止めずに手放す（次の点へ向かうなら新しい経路が届くまで進み、敵がいれば戦闘が止める）
                this.mover.Release();
                this.Enter(FarmState.Search, $"巡回の点 {this.spotIndex + 1} で探しています");
                break;
            case Mover.Result.Failed:
                Core.DebugLog.Current?.Line("移動", $"巡回の点 {this.spotIndex + 1} へ行けませんでした：{this.mover.Problem}");
                this.mover.Stop(this.nav);
                if (++this.spotFailures >= this.patrol!.Spots.Count)
                {
                    this.FinishWhenClear(FarmState.Stopped, "move", $"巡回の点へ移動できません：{this.mover.Problem}", []);
                    return;
                }

                this.NextSpot(now);
                break;
            default:
                this.Status = this.mover.Status;
                break;
        }
    }

    /// <summary>
    /// 飛べるエリアで、今の巡回の点の上に屋根・木・岩があるかを、近づいてから（RoofCheckDistance 以内）ゲームの当たり判定で1回確かめる。
    /// あれば上空ではなく地面の点へ向かい直す（上空の行き先が物の中になって経路が引けないのを避ける）。
    /// </summary>
    private void CheckRoof()
    {
        if (!this.world.CanFlyHere || this.roofChecked.Contains(this.spotIndex))
            return;
        var spot = this.patrol!.Spots[this.spotIndex];
        var me = this.world.MyPosition;
        if (Vector2.Distance(new(me.X, me.Z), new(spot.Center.X, spot.Center.Z)) > this.limits.RoofCheckDistance)
            return;
        this.roofChecked.Add(this.spotIndex);
        if (this.world.SkyAbove(spot.Center) is { } sky && sky.TopY > spot.Center.Y + this.limits.RoofMargin)
        {
            this.roofed.Add(this.spotIndex);
            Core.DebugLog.Current?.Line("探索", $"巡回の点 {this.spotIndex + 1} の上に物があります（{sky.TopY - spot.Center.Y:0}m 上：屋根・木・岩）。上空ではなく地面の点へ向かいます");
            this.mover.Start(this.world, spot.Center, this.limits.SpotRange, $"巡回の点 {this.spotIndex + 1}/{this.patrol.Spots.Count}（地面）");
            this.Status = this.mover.Status;
        }
    }

    /// <summary>
    /// 歩いて行けるか（飛べないエリアだけ：vnavmesh の「本来の地面とつながった床」の上にいるか。メッシュで範囲を絞る：D9）。
    /// 飛べるエリアは、戦闘の部品（Engagement）が降りる場所を探すので見ない。
    /// </summary>
    private bool Reachable(MobSnapshot m)
    {
        if (this.world.CanFlyHere)
            return true;
        return this.nav.NearestPointReachable(m.Position, 3f, 6f) is { } q
            && Vector2.Distance(new(q.X, q.Z), new(m.Position.X, m.Position.Z)) <= 3f;
    }

    /// <summary>巡回の点に着いた。いなければ、点が2つ以上なら次の点へ、1つだけなら再出現を待つ。</summary>
    private void TickSearch(DateTime now, PlayerContext me)
    {
        if (this.TryPickTarget(now, me, force: true))
            return;

        // 着いた点にいない。点が2つ以上なら待たずに次の点へ。1つだけなら再出現を待つ
        if (this.patrol!.Spots.Count >= 2)
        {
            this.NextSpot(now);
            return;
        }

        this.Enter(FarmState.SpawnWait, "回る点が1つだけなので、ここで再出現を待ちます");
    }

    /// <summary>回る点が1つだけのとき、その点で再出現を待つ。SpawnWait が過ぎたら「同じ点へもう1回来た」として NextSpot で数える。</summary>
    private void TickSpawnWait(DateTime now, PlayerContext me)
    {
        if (this.TryPickTarget(now, me))
            return;
        this.Status = $"再出現を待っています（{(now - this.stateSince).TotalSeconds:0}/{this.limits.SpawnWait.TotalSeconds:0}秒）";
        if (now - this.stateSince > this.limits.SpawnWait)
            this.NextSpot(now);
    }

    /// <summary>GiveUpFor を過ぎた「諦めた個体」を、また狙えるように戻す（討伐対象・反撃の相手の両方）。</summary>
    private void ExpireGiveUps(DateTime now)
    {
        foreach (var (id, at) in this.giveUpAt.ToList())
        {
            if (now - at > this.limits.GiveUpFor)
                this.giveUpAt.Remove(id);
        }
    }

    /// <summary>
    /// こちらから仕掛けてよい個体を探して狙う（近い順・敵視リストにいる物を先に。飛べないエリアでは歩いて行けない個体を見送る）。
    /// 狙ったら見つけた位置を覚え（S6）、true。反撃（攻撃してきた敵）はここではなく TryRetaliate が扱う。
    /// </summary>
    private bool TryPickTarget(DateTime now, PlayerContext me, bool force = false)
    {
        if (!force && now - this.lastScan < this.limits.ScanInterval)
            return false;
        this.lastScan = now;

        this.ExpireGiveUps(now);

        var range = this.world.Flying ? this.limits.FlyingSearchRange : this.limits.SearchRange;
        var haters = this.world.Haters.ToHashSet();
        var judged = this.world.Mobs
            .Where(m => !this.giveUpAt.ContainsKey(m.GameObjectId))
            .Where(m => Vector2.Distance(new Vector2(m.Position.X, m.Position.Z), new Vector2(me.Position.X, me.Position.Z)) <= range)
            .Select(m => (Mob: m, J: this.Judge(m, me)))
            .ToList();

        // 計画の名前の敵が近くにいるのに狙えないときは、理由を 1 体につき 1 回だけ記録に残す
        // （記録に理由が無いと、狙わなかった原因をソースから推し量るしかなくなる）
        foreach (var (m, j) in judged)
        {
            if (!j.Attackable && !m.Dead && m.Hp > 0 && this.plan!.Areas.Any(a => a.NameId == m.NameId) && this.rejectLogged.Add(m.GameObjectId))
                Core.DebugLog.Current?.Line("探索", $"{m.Name}（名前 {m.NameId}・基本 {m.BaseId}・ID {m.GameObjectId:X}・{Vector3.Distance(m.Position, me.Position):0}m）は狙いません：{string.Join("・", j.Reasons)}");
        }

        var candidates = judged
            .Where(x => x.J.Attackable)
            .OrderByDescending(x => haters.Contains(x.Mob.GameObjectId))
            .ThenBy(x => Vector3.Distance(x.Mob.Position, me.Position))
            .Select(x => x.Mob)
            .ToList();
        MobSnapshot? pick = null;
        foreach (var candidate in candidates)
        {
            if (this.Reachable(candidate))
            {
                pick = candidate;
                break;
            }

            this.giveUpAt[candidate.GameObjectId] = now;
            Core.DebugLog.Current?.Line("探索", $"{candidate.Name} は歩いて行ける地面（vnavmesh のつながった床）の上にいないので見送ります");
        }

        if (pick == null)
            return false;

        // 自分の巡回の移動を止めてから狙う（止めないと、戦闘の経路が届くまで巡回の点へ進み続ける）
        this.mover.Stop(this.nav);
        if (this.found.Add(new FoundSpot(me.Territory, pick.NameId, pick.BaseId, pick.Position.X, pick.Position.Y, pick.Position.Z, pick.Level, me.GameVersion, new DateTimeOffset(now))))
            Core.DebugLog.Current?.Line("探索", $"見つけた位置を覚えました：{pick.Name}（{pick.Position.X:0},{pick.Position.Y:0},{pick.Position.Z:0}）");
        this.targetId = pick.GameObjectId;
        this.targetName = pick.NameId;
        this.targetBase = pick.BaseId;
        this.targetSince = now;
        this.seenThisLap = true;
        this.engage.Start(this.world, new SnapshotFoe(pick));
        this.Enter(FarmState.Engage, $"{pick.Name} を狙います");
        Core.DebugLog.Current?.Line("収集", $"狙う：{pick.Name}（名前 {pick.NameId}・基本 {pick.BaseId}・Lv{pick.Level}・ID {pick.GameObjectId:X}・{Vector3.Distance(pick.Position, me.Position):0}m）");
        return true;
    }

    /// <summary>
    /// 攻撃してきた敵（敵視リスト UIState.Hater にいる、生きた・敵対する・ターゲットできる敵）。近い順。
    /// 計画・FATE・イベント・他人の獲物の判定は見ない（攻撃してきた以上、倒す：D1）。
    /// 「諦めた個体」も除かない（除くと、歩いて届かず乗れない〔戦闘中〕と諦めた敵に殴られたまま、次の狙い・移動へ進む）。
    /// </summary>
    private List<MobSnapshot> Attackers(DateTime now)
    {
        var me = this.world.MyPosition;
        return this.world.Haters
            .Select(id => this.world.FindMob(id))
            .OfType<MobSnapshot>()
            .Where(m => m.IsCombatant && !m.Dead && m.Hp > 0 && m.Enemy && m.Targetable)
            .OrderBy(m => Vector3.Distance(m.Position, me))
            .ToList();
    }

    /// <summary>反撃の割り込み（D1・D4）。反撃を始めたら true。準備中（RSR を確かめる前）は始めない。</summary>
    private bool TryRetaliate(DateTime now)
    {
        if (this.State == FarmState.Arm)
            return false;   // RSR の設定（範囲攻撃 Off 等）を確かめる前は戦えない。TickArm が済めば次のフレームで反撃に入る
        var attackers = this.Attackers(now);
        if (attackers.Count == 0)
            return false;
        if (this.State == FarmState.Engage && attackers.Any(a => a.GameObjectId == this.targetId))
            return false;   // いま戦っている相手（反撃の相手・討伐対象）が攻撃してきている → そのまま倒す（倒した後に残りへ：AfterFight）
        this.StartRetaliation(attackers[0], now);
        return true;
    }

    /// <summary>攻撃してきた敵への反撃を始める（今の移動・テレポの頼み・狙いをやめ、相手を狙う。乗っていれば Engagement が降りる）。</summary>
    private void StartRetaliation(MobSnapshot attacker, DateTime now)
    {
        // 戻り先は最初に決めたまま（攻撃者が続けて来ても上書きしない）。
        //  ・準備・テレポ・地図待ちの途中（巡回の計画がまだ無い）→ 準備からやり直す
        //  ・巡回の点へ向かう途中・再出現待ち → 同じ巡回の点へ向かい直す（その点に来ないまま「1回来た」と数えない：D3）
        //  ・索敵・討伐・所持数確認の途中 → 近くを探す
        this.resumeState ??= this.State is FarmState.Arm or FarmState.Travel or FarmState.NavWait || this.patrol == null
            ? FarmState.Arm
            : this.State is FarmState.ToSpot or FarmState.SpawnWait ? FarmState.ToSpot : FarmState.Search;
        this.mover.Stop(this.nav);
        this.teleportRequested = false;
        if (this.State == FarmState.Engage && this.targetId != 0)
            this.DropTarget(now, giveUp: false);
        this.targetId = attacker.GameObjectId;
        this.targetName = attacker.NameId;
        this.targetBase = attacker.BaseId;
        this.targetSince = now;
        this.retaliating = true;
        this.retaliationLostSince = null;
        this.engage.Start(this.world, new SnapshotFoe(attacker), holdForAttacker: true);
        this.Enter(FarmState.Engage, this.pendingFinish is { } pf
            ? $"{pf.Message}。攻撃してきた敵を倒してから終えます（{attacker.Name}）"
            : $"攻撃してきた {attacker.Name} に反撃します");
        Core.DebugLog.Current?.Line("反撃", $"反撃：{attacker.Name}（名前 {attacker.NameId}・基本 {attacker.BaseId}・計画の判定 {TargetJudge.Name(this.Judge(attacker, this.world.Player).Verdict)}）");
    }

    /// <summary>
    /// 倒した・手を引いた後の行き先（上から最初に当てはまるもの）：まだ攻撃してくる敵がいれば反撃 → 終わり方が決まっていれば終える →
    /// 反撃の続きなら戻り先へ（巡回の点へ向かい直す・準備・近くを探す）→ ふつうに倒したなら近くを探す。
    /// </summary>
    private void AfterFight(DateTime now)
    {
        var attackers = this.Attackers(now);
        if (attackers.Count > 0)
        {
            this.StartRetaliation(attackers[0], now); // 戻り先は最初に決めたまま
            return;
        }

        if (this.pendingFinish is { } pf)
        {
            this.pendingFinish = null;
            this.resumeState = null;
            this.Finish(pf.State, pf.Code, pf.Message, pf.Details);
            return;
        }

        if (this.resumeState is { } rs)
        {
            this.resumeState = null;
            if (rs == FarmState.ToSpot)
            {
                // 向かっていた（待っていた）巡回の点へ向かい直す。点の番号は進めない
                Core.DebugLog.Current?.Line("反撃", $"反撃を終えました。巡回の点 {this.spotIndex + 1} へ向かい直します");
                this.GoToSpot(now);
                return;
            }

            this.Enter(rs, rs == FarmState.Arm ? "反撃を終えました。狩り場への移動を続けます" : "反撃を終えました。近くの対象を探します");
            return;
        }

        this.Enter(FarmState.Search, "近くの次の対象を探しています");
    }

    /// <summary>
    /// 危険でない理由で終える（target・noprogress・nofind・move・mesh・travel・height・full）。攻撃してきた敵がいれば、終わり方を覚えて
    /// 先に倒しきる（終えた後に殴られたまま次へ進まない：D1）。利用者の停止・保持の理由は待たずに Finish を使う。
    /// </summary>
    private void FinishWhenClear(FarmState state, string code, string message, IReadOnlyList<string> details)
    {
        var now = this.world.Now;
        var attackers = this.Attackers(now);
        if (attackers.Count == 0)
        {
            this.pendingFinish = null;
            this.resumeState = null;
            this.Finish(state, code, message, details);
            return;
        }

        if (this.pendingFinish == null)
        {
            this.pendingFinish = (state, code, message, details);
            Core.DebugLog.Current?.Line("反撃", $"{message}。攻撃してきた敵（{attackers.Count} 体）を倒してから終えます");
        }

        if (this.State == FarmState.Arm)
            return;   // 準備（RSR の確かめ）が済んでから反撃する（Tick の⑥）。攻撃者がいなくなれば Tick の③で終える
        if (this.State == FarmState.Engage && attackers.Any(a => a.GameObjectId == this.targetId))
        {
            this.retaliating = true;   // いま戦っている相手が攻撃してきている。そのまま倒す（以後は反撃として扱い、判定し直さない）
            this.retaliationLostSince = null;
            this.engage.HoldForAttacker = true;
            return;
        }

        this.StartRetaliation(attackers[0], now);
    }

    /// <summary>
    /// 接近・討伐・反撃。毎フレーム相手を引き直し、倒した・見えなくなった・手を引くべきになったら AfterFight へ。
    /// 討伐対象は攻撃の直前にも判定し直す。反撃の相手は判定し直さず（D1）、こちらを狙うのをやめたら RetaliationLostGrace で手を引く。
    /// </summary>
    private void TickEngage(DateTime now, PlayerContext me)
    {
        // 毎フレーム GameObjectId で引き直し、名前・基本 ID が同じかを確かめる（枠の再利用で別の敵を指さない）
        var mob = this.world.FindMob(this.targetId);
        if (mob == null || mob.NameId != this.targetName || mob.BaseId != this.targetBase)
        {
            var why = mob == null ? "見えなくなりました" : $"同じ ID の枠に別の敵（名前 {mob.NameId}・基本 {mob.BaseId}）が入っています";
            Core.DebugLog.Current?.Line("収集", $"狙っていた敵が{why}。狙いを外します");
            this.EndFight(now, giveUp: false);
            return;
        }

        if (mob.Dead || mob.Hp == 0)
        {
            this.Kills++;
            this.countAtKill = this.lastCount;
            Core.DebugLog.Current?.Line("収集", $"{(this.retaliating ? "反撃で倒しました" : "倒しました")}：{mob.Name}（{this.Kills} 体目）");
            this.Killed?.Invoke(mob);

            // その場の討伐は、討伐対象を倒したことを進展に数える（反撃で倒した数は数えない）
            if (this.plan!.AdHoc && !this.retaliating)
                this.lastProgress = now;
            if (this.plan.AdHoc)
            {
                this.EndFight(now, giveUp: false); // 品が無いので所持数を待たない
                return;
            }

            this.DropTarget(now, giveUp: false);
            this.retaliating = false;
            this.retaliationLostSince = null;
            this.Enter(FarmState.CountCheck, "所持数を確かめています");
            return;
        }

        if (this.retaliating)
        {
            // 反撃中は判定し直さない（D1）。こちらを狙うのをやめた（敵視リストから消えたまま RetaliationLostGrace）なら手を引く
            if (!this.world.Haters.Contains(this.targetId))
            {
                this.retaliationLostSince ??= now;
                if (now - this.retaliationLostSince.Value >= this.limits.RetaliationLostGrace)
                {
                    Core.DebugLog.Current?.Line("反撃", $"{mob.Name} はこちらを狙うのをやめました。反撃をやめます");
                    this.EndFight(now, giveUp: false);
                    return;
                }
            }
            else
            {
                this.retaliationLostSince = null;
            }
        }

        // 討伐対象は攻撃の直前にも判定し直す（他人の獲物・イベント個体・範囲外・計画外・判別できない、になったら手を引く）
        var j = this.Judge(mob, me);
        if (!this.retaliating && !j.Attackable)
        {
            Core.DebugLog.Current?.Line("収集", $"{mob.Name} は攻撃してよい個体ではなくなりました（{TargetJudge.Name(j.Verdict)}：{string.Join("・", j.Reasons)}）。手を引きます");
            this.EndFight(now, giveUp: true);
            return;
        }

        if (now - this.targetSince > this.limits.TargetLimit)
        {
            if (this.retaliating)
            {
                this.KeepRetaliating(mob, now, $" に {this.limits.TargetLimit.TotalMinutes:0}分かけても倒せていませんが、");
                return;
            }

            Core.DebugLog.Current?.Line("収集", $"{mob.Name} に {this.limits.TargetLimit.TotalMinutes:0}分かけても倒せません。この個体は諦めます");
            this.EndFight(now, giveUp: true);
            return;
        }

        // 巡回の移動が止まったと確かめるまでは、近づく移動も攻撃も始めない（R6：停止確認前に別の経路を始めない）
        if (this.mover.StopPending)
        {
            this.rsr.Suppress();
            this.Status = this.mover.StopProblem ?? "巡回の移動が止まったかを確かめています";
            return;
        }

        var result = this.engage.Tick(this.world, this.rsr, this.nav, new SnapshotFoe(mob), out var status, this.YieldMovement?.Invoke() == true);

        // Henched を頼むのは、戦う段に入り、ハードターゲットが自分の対象になっていると読めたときだけ（ほかの敵が狙われたまま Henched にしない：R3）
        if (this.engage.AttackPhase && this.world.HardTargetId == this.targetId && now >= this.contestedUntil)
            this.rsr.Engage();
        else
            this.rsr.Suppress();

        switch (result)
        {
            case Engagement.Result.Running:
                this.Status = this.retaliating ? $"反撃：{status}" : status;
                break;
            case Engagement.Result.Stalled when this.retaliating:
                this.KeepRetaliating(mob, now, this.engage.StallReason ?? $"の HP が {StallWatch.Limit.TotalSeconds:0}秒減りませんが、");
                break;
            case Engagement.Result.Stalled:
                Core.DebugLog.Current?.Line("収集", $"{mob.Name}{this.engage.StallReason ?? $"の HP が {StallWatch.Limit.TotalSeconds:0}秒減りません。"}この個体は諦めます");
                this.EndFight(now, giveUp: true);
                break;
            case Engagement.Result.NavFailed when this.retaliating:
                this.KeepRetaliating(mob, now, $" へ近づけません（{this.engage.Problem}）が、");
                break;
            case Engagement.Result.NavFailed:
                // 経路探索を使えない（vnavmesh が無い・古い）。止めずにこの相手を諦める（D10）
                Core.DebugLog.Current?.Line("収集", $"{mob.Name} へ近づけません：{this.engage.Problem}。この相手は諦めます");
                this.EndFight(now, giveUp: true);
                break;
        }
    }

    /// <summary>
    /// 反撃の相手は諦めない（攻撃してきた敵は、倒してから乗る・移動する）。
    /// HP が減らない・時間がかかる・近づけないときも離れず、近づき方を最初からやり直す（降りる場所の候補・ジャンプ・HP の見張り・時間を数え直す）。
    /// 反撃をやめるのは、倒したときと、相手がこちらを狙うのをやめた（敵視リストから消えたまま RetaliationLostGrace）ときだけ。記録は相手ごとに 30 秒に 1 回。
    /// </summary>
    private void KeepRetaliating(MobSnapshot mob, DateTime now, string why)
    {
        if (this.keepLoggedFor != mob.GameObjectId || now - this.keepLoggedAt >= TimeSpan.FromSeconds(30))
        {
            this.keepLoggedFor = mob.GameObjectId;
            this.keepLoggedAt = now;
            Core.DebugLog.Current?.Line("反撃", $"{mob.Name}{why}まだ攻撃してくるので、離れずに反撃を続けます（近づき方をやり直します）");
        }

        this.targetSince = now;
        this.engage.Start(this.world, new SnapshotFoe(mob), holdForAttacker: true);
    }

    /// <summary>倒した直後。所持数の変化を CountWait だけ待ってから行き先を決める（落とさないこともあるので、待つのはこの時間だけ）。</summary>
    private void TickCountCheck(DateTime now, PlayerContext me)
    {
        if (this.lastCount <= this.countAtKill && now - this.stateSince < this.limits.CountWait)
        {
            this.Status = "所持数の変化を待っています";
            return;
        }

        this.AfterFight(now);
    }

    // ------------------------------------------------------------------
    // 後片づけ
    // ------------------------------------------------------------------

    /// <summary>今の相手から手を引き（<paramref name="giveUp"/> なら GiveUpFor の間は狙い直さない）、反撃の印を消して行き先を決める（AfterFight）。</summary>
    private void EndFight(DateTime now, bool giveUp)
    {
        this.DropTarget(now, giveUp);
        this.retaliating = false;
        this.retaliationLostSince = null;
        this.AfterFight(now);
    }

    /// <summary>狙いを外す（攻撃を抑止し、自分の近づく移動を止め、自分が置いたハードターゲットだけ外す）。</summary>
    private void DropTarget(DateTime now, bool giveUp)
    {
        this.rsr.Suppress();
        if (giveUp && this.targetId != 0)
            this.giveUpAt[this.targetId] = now;
        this.engage.StopApproach(this.nav);
        this.engage.Forget(this.world);
        this.targetId = 0;
    }

    /// <summary>新しい操作を止める（自分の移動・近づく移動・狙い・テレポの頼み・オートアタック）。各部品が失敗しても残りへ進む（R6）。</summary>
    private void Halt()
    {
        Try("巡回の移動の停止", () => this.mover.Stop(this.nav));
        Try("近づく移動の停止", () => this.engage.StopApproach(this.nav));
        Try("狙いの解除", () => this.engage.Forget(this.world));
        this.targetId = 0;
        this.teleportRequested = false;
        if (this.world.AutoAttacking == true)
            Try("オートアタックの停止", () => this.world.ToggleAutoAttack());
    }

    private static void Try(string what, Action act)
    {
        try
        {
            act();
        }
        catch (Exception ex)
        {
            Core.DebugLog.Current?.Line("後片づけ", $"{what}で例外：{ex.GetType().Name}: {ex.Message}（ほかの後片づけは続けます）");
        }
    }

    /// <summary>
    /// 終える。RSR は、保持の理由（<see cref="HoldCodes"/>）・攻撃してきた敵が残っている（利用者の「元に戻す」以外）なら Off のまま控えを保持し、
    /// 複数品の途中なら Off のまま持ち続け、それ以外は元の設定へ戻す（読み直して確かめる）。
    /// </summary>
    private void Finish(FarmState state, string code, string message, IReadOnlyList<string> details)
    {
        this.pendingFinish = null;
        this.resumeState = null;
        this.retaliating = false;
        this.retaliationLostSince = null;
        this.standbyKind = null;

        var now = this.world.Now;
        this.rsr.Suppress();
        this.Halt();

        // RSR を触るのは制御中のときだけ（保持中・復元中の控えを、利用者の操作以外で戻したり捨てたりしない）
        var attacked = this.world.Haters.Any(id => this.world.FindMob(id) is { Dead: false, Hp: > 0 });
        var hold = this.rsr.Controlling && (HoldCodes.Contains(code) || (attacked && code != "user"));
        if (hold)
            Try("RSR の保持", () => this.rsr.Hold(attacked && !HoldCodes.Contains(code) ? "敵に狙われたまま止めたため" : message));
        else if (this.rsr.Controlling && this.KeepRsrOnFinish && code != "user")
            this.rsr.Suppress(); // 続けて次の品を集める（品と品のあいだで元の Auto 等へ戻すと、近くの敵を殴りうる）。Off のまま控えを持ち続ける
        else if (this.rsr.Controlling)
            Try("RSR の復元", () => this.rsr.Restore(now));

        // 保持に入っても RSR が Henched のままだと読めていれば（Off の命令が効かない）、このフレームのうちに敵へのターゲットも外す
        if (this.rsr.HeldButHenched)
            this.ClearCombatTarget("止めましたが RSR が Henched のままなので");

        var moveNote = this.MoveStopPending ? "（移動の停止を確かめています）" : string.Empty;
        this.Stop = new StopInfo(code, message + moveNote, now, details, hold);
        this.Enter(state, message);
        Core.DebugLog.Current?.Line("収集", $"{(state == FarmState.Done ? "完了" : "停止")}：{message}{moveNote}{(hold ? "（RSR は Off のまま控えを保持）" : string.Empty)}"
            + (details.Count > 1 ? "（" + string.Join(" / ", details) + "）" : string.Empty));
    }

    private void Enter(FarmState state, string status)
    {
        this.State = state;
        this.Status = status;
        this.stateSince = this.world.Now;
        if (state == FarmState.Arm)
            this.armNoticeAt = this.world.Now; // 準備に入るたびに「長引いた」の記録を数え直す
    }

    /// <summary>1回の実行の値を初期値に戻す（Start の最初）。</summary>
    private void ResetRun()
    {
        this.pendingFinish = null;
        this.resumeState = null;
        this.retaliating = false;
        this.retaliationLostSince = null;
        this.standbyKind = null;
        this.lastTakeTry = DateTime.MinValue;
        this.armNoticeAt = DateTime.MinValue;
        this.navReloads = 0;
        this.navIdleSince = null;
        this.lastScan = DateTime.MinValue;
        this.lastAutoAttackToggle = DateTime.MinValue;
        this.startCount = 0;
        this.lastCount = 0;
        this.countAtKill = 0;

        this.patrol = null;
        this.spotIndex = 0;
        this.Kills = 0;
        this.Stop = null;
        this.targetId = 0;
        this.giveUpAt.Clear();
        this.rejectLogged.Clear();
        this.teleportRequested = false;
        this.teleportAttempts = 0;
        this.lapsWithout = 0;
        this.spotFailures = 0;
        this.contestedUntil = DateTime.MinValue;
    }
}
