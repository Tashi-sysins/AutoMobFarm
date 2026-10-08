using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace AutoMobFarm.Farm;

/// <summary>
/// BMR（BossMod Reborn）の AI に、戦う段だけ足を任せる（範囲攻撃の自動回避・敵へ近づくなど、基本の戦い方）。
///
/// 役目の分け方：
///  ・攻撃とターゲットは RSR（Henched）と AutoMobFarm の役目。BMR には攻撃もターゲットの変更もさせない（ForbidActions・ManualTarget を True）。
///    「仕掛けるのは対象だけ・攻撃してきた敵には反撃」（feedback-retaliate-attackers）は今までどおり AutoMobFarm が決める。
///  ・BMR に任せるのは足だけ：範囲攻撃を避ける・戦っている相手へ寄る（FollowTarget・FollowDuringCombat を True。寄る距離は近接 2.6m・遠隔 20m）。
///  ・戦う段（乗っていない・狙いの敵のそばで降りてハードターゲットを置いた後：FarmRunner.InAttackPhase）だけ ForbidMovement を False にする。
///    移動・索敵・乗っている間は True（vnavmesh の移動と足を取り合わない）。
///  ・戦う段で BMR が動かしたがっている・いまの位置が範囲攻撃の中のときは、AutoMobFarm の近づく移動を出さない（<see cref="YieldMovement"/>）。
///    vnavmesh が経路を走っている間は BMR が足を上書きしない（BMR の MovementOverride）ので、こちらが止めないと範囲攻撃の中へ歩いてしまう。
///
/// 預かり方（利用者の設定を壊さない）：
///  ・設定は BossMod.Configuration の IPC に save=false で書く（BMR の設定ファイルへは書かない）。書く前の値を控え、終わったら元へ戻す。
///    利用者が途中で変えた値（いま入っている値が、こちらが入れた値と違う）は、戻すときに上書きしない。
///  ・預かっている間に BMR の設定が保存された（ファイルにこちらの一時の値が入った）ら、元へ戻すときにもう一度保存させる。
///  ・AI が切れていたら「自分を追う」（SwitchToFollow(0)＝誰も追わない）で入れ、終わったら切る。初めから入っていたら切らない。
///    パーティの誰かを追っていたら自分を追うように替え（戦う段にその人へ寄って行かないように）、終わったら元の相手へ戻す。
///  ・AI の入り切りで BMR の自動ローテのプリセットが外れる（SwitchToIdle が外す）ので、入れる前の値を控えて戻す。
///  ・途中で外から変えられたら、止めずに入れ直す（feedback-recover-dont-stop。RSR と同じ扱い）。数は <see cref="ExternalChanges"/>。
///  ・BMR が無い・本家 BossMod と同時に読み込まれている・設定を入れられないときは、BMR なしで続ける（収集は止めない）。理由は <see cref="Problem"/>。
///  ・収集を終えた・止めた・コンテンツ参加中（利用者や AutoDuty などが BMR を使う）は手放す（<see cref="Release"/>）。
/// </summary>
public sealed class BmrAssist
{
    /// <summary>設定を読み直して、外から変えられていたら入れ直す間隔（戦う段の出入りは間隔を待たずにすぐ反映する）。</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>近接の役割で相手へ寄る距離（BMR の既定。近接の位置取りと同じ）。</summary>
    public const float MeleeDistance = 2.6f;

    /// <summary>遠隔の役割で相手へ寄る距離（技の射程 25m 前後の内側で、寄りすぎない）。</summary>
    public const float RangedDistance = 20f;

    private readonly IBossModControl bmr;
    private readonly List<(string Key, string Before, string Applied)> lease = []; // 入れた順（戻すときは逆の順）
    private bool aiOnByUs;
    private int? masterBefore;
    private string? presetBefore;
    private DateTime? modifiedAtLease;
    private DateTime lastCheck = DateTime.MinValue;
    private bool? lastCombat;
    private bool? lastMelee;

    public BmrAssist(IBossModControl bmr) => this.bmr = bmr;

    /// <summary>BMR の設定を預かっているか（戻すまで true）。</summary>
    public bool Leased { get; private set; }

    /// <summary>いま BMR に足を任せているか（戦う段で、設定が入ったと確かめられた）。</summary>
    public bool Combat { get; private set; }

    /// <summary>戦う段で、こちらの近づく移動を控えるか（BMR が動かしたがっている・いまの位置が範囲攻撃の中）。</summary>
    public bool YieldMovement { get; private set; }

    /// <summary>BMR を使えない・設定を入れられない理由（画面用。使えていれば null）。収集は止めない。</summary>
    public string? Problem { get; private set; }

    /// <summary>預かっている間に外から変えられて入れ直した回数（画面・記録用）。</summary>
    public int ExternalChanges { get; private set; }

    /// <summary>
    /// 毎フレーム（FarmRunner・GoalRunner の後）。<paramref name="active"/>＝収集中（条件待ちのコンテンツ参加中を除く）、
    /// <paramref name="combat"/>＝戦う段で乗っていない、<paramref name="melee"/>＝近接の役割、<paramref name="me"/>＝自分の位置。
    /// </summary>
    public void Tick(DateTime now, bool active, bool combat, bool melee, Vector3 me)
    {
        if (!active)
        {
            if (this.Leased)
                this.Release("収集を終えた・止めた・コンテンツ参加中");
            this.Combat = false;
            this.YieldMovement = false;
            this.lastCombat = null;
            return;
        }

        if (this.bmr.Unusable is { } why)
        {
            // BMR なしで続ける（止めない）。預かりは残す（読み込まれ直したら入れ直す・終わったら戻す）
            this.Problem = $"{why}。BMR なしで続けます（範囲攻撃は避けません）";
            this.Combat = false;
            this.YieldMovement = false;
            return;
        }

        // 戦う段の出入り・役割の変化はすぐ反映する（足を任せる・取り返すのを1秒遅らせない）。それ以外は1秒ごとに読み直す
        if (combat != this.lastCombat || melee != this.lastMelee || now - this.lastCheck >= CheckInterval)
        {
            this.lastCheck = now;
            this.lastCombat = combat;
            this.lastMelee = melee;
            this.Apply(combat, melee);
        }

        this.Combat = combat && this.Leased && this.Problem == null;
        this.YieldMovement = this.Combat && (this.bmr.WantsToMove == true || this.bmr.IsSafe(me) == false);
    }

    /// <summary>入れる設定（入れる順）。ForbidActions・ManualTarget を先に入れ、足の許可（ForbidMovement）は最後。</summary>
    public static IEnumerable<(string Key, string Value)> Wanted(bool combat, bool melee)
    {
        yield return ("ForbidActions", bool.TrueString);              // 攻撃は RSR に任せる（BMR に技を使わせない）
        yield return ("ManualTarget", bool.TrueString);               // ターゲットは AutoMobFarm が置く（BMR に変えさせない）
        yield return ("AutoAFK", bool.FalseString);
        yield return ("ForbidAIMovementMounted", bool.TrueString);    // 乗っている間は動かさない（Idle while mounted）
        yield return ("FollowOutOfCombat", bool.FalseString);
        yield return ("FollowDuringCombat", bool.TrueString);
        yield return ("FollowDuringActiveBossModule", bool.TrueString);
        yield return ("FollowTarget", bool.TrueString);               // 戦っている相手へ寄る
        yield return ("MaxDistanceToTarget", (melee ? MeleeDistance : RangedDistance).ToString(CultureInfo.CurrentCulture));
        yield return ("ForbidMovement", combat ? bool.FalseString : bool.TrueString); // 戦う段だけ足を任せる
    }

    /// <summary>
    /// 手放す：自分で入れた AI を切る（追う相手を替えたなら戻す）→ 外れたプリセットを戻す → 設定を入れたのと逆の順で元へ戻す
    /// （利用者が途中で変えた値は上書きしない）。BMR が読み込まれていなければ、控えを捨てる（BMR のファイルには書いていないので、次に読み込んだとき元の値）。
    /// </summary>
    public void Release(string why)
    {
        if (!this.Leased)
            return;

        if (this.bmr.Unusable is { } gone)
        {
            Core.DebugLog.Current?.Line("BMR", $"手放します（{why}）。BMR を読めないので戻す操作はしません（{gone}。設定はファイルへ書いていないので、BMR の読み込み直しで元の値です）");
            this.Forget();
            return;
        }

        var problems = new List<string>();

        // ① AI：自分で入れたなら切る。追う相手を替えたなら戻す
        if (this.aiOnByUs)
        {
            if (this.bmr.AiOn == true && !this.bmr.AiOff())
                problems.Add("AI を切れません");
        }
        else if (this.masterBefore is { } master && this.bmr.AiOn == true && !this.bmr.Follow(master))
        {
            problems.Add("AI の追う相手を戻せません");
        }

        // ② 自動ローテのプリセット（AI の入り切りで外れる）
        if (!string.IsNullOrEmpty(this.presetBefore) && this.bmr.ActivePreset != this.presetBefore && !this.bmr.SetActivePreset(this.presetBefore))
            problems.Add($"プリセット「{this.presetBefore}」を戻せません");

        // ③ 設定。預かっている間に BMR の設定が保存されていたら（ファイルにこちらの一時の値が入った）、最後に戻す1つで保存させて元の値を書き戻す
        var saved = this.bmr.LastModified is { } lm && this.modifiedAtLease is { } m0 && lm != m0;
        for (var i = this.lease.Count - 1; i >= 0; i--)
        {
            var (key, before, applied) = this.lease[i];
            var current = this.bmr.Read(key);
            var saveNow = saved && i == 0;
            if (current == null)
            {
                problems.Add($"{key} を読めません");
                continue;
            }

            if (Same(current, before) && !saveNow)
                continue;
            if (!Same(current, applied) && !Same(current, before))
                continue; // 利用者が途中で変えた値は上書きしない
            if (!this.bmr.Write(key, before, saveNow))
                problems.Add($"{key} を戻せません");
        }

        Core.DebugLog.Current?.Line("BMR", problems.Count == 0
            ? $"BMR の設定を元へ戻しました（{why}{(saved ? "。預かっている間に保存されたので、戻した値で保存し直しました" : string.Empty)}）"
            : $"BMR を手放しました（{why}）。戻せなかったもの：{string.Join("・", problems)}");
        this.Forget();
    }

    /// <summary>BMR の設定の値が同じか（真偽は大文字小文字を見ない・数は 0.01 まで）。</summary>
    public static bool Same(string? a, string? b)
    {
        if (a == null || b == null)
            return a == b;
        if (bool.TryParse(a, out var x) && bool.TryParse(b, out var y))
            return x == y;
        if (float.TryParse(a, NumberStyles.Float, CultureInfo.CurrentCulture, out var f) && float.TryParse(b, NumberStyles.Float, CultureInfo.CurrentCulture, out var g))
            return MathF.Abs(f - g) < 0.01f;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private void Apply(bool combat, bool melee)
    {
        var problems = new List<string>();
        if (!this.Leased)
        {
            this.presetBefore = this.bmr.ActivePreset;
            this.modifiedAtLease = this.bmr.LastModified;
            this.Leased = true;
            Core.DebugLog.Current?.Line("BMR", $"BMR の AI の設定を預かります（攻撃・ターゲットは任せず、戦う段の足だけ任せる。自動ローテのプリセット「{this.presetBefore ?? "（読めない）"}」）");
        }

        foreach (var (key, value) in Wanted(combat, melee))
        {
            if (!this.Set(key, value))
                problems.Add(key);
        }

        // 設定を入れてから AI を入れる（最初のフレームから、攻撃・ターゲット・移動の禁止が効いている状態で動かす）
        if (problems.Count == 0)
        {
            switch (this.bmr.AiOn)
            {
                case false:
                    if (this.bmr.Follow(0))
                    {
                        this.aiOnByUs = true;
                        Core.DebugLog.Current?.Line("BMR", "BMR の AI を入れました（自分を追う＝誰も追わない）");
                    }
                    else
                    {
                        problems.Add("AI を入れられません");
                    }

                    break;
                case true when this.bmr.MasterSlot is { } m && m != 0:
                    // パーティの誰かを追っていると、戦う段にその人へ寄って行く。自分を追うように替え、手放すとき元へ戻す
                    this.masterBefore ??= m;
                    if (this.bmr.Follow(0))
                        Core.DebugLog.Current?.Line("BMR", $"BMR の AI が追う相手（パーティの {m} 番）を自分に替えました（手放すとき戻します）");
                    else
                        problems.Add("AI の追う相手を自分にできません");
                    break;
                case null:
                    problems.Add("AI の状態を読めません");
                    break;
            }
        }

        var text = problems.Count == 0 ? null : $"BMR に設定を入れられません（{string.Join("・", problems)}）。BMR なしで続けます（範囲攻撃は避けません）";
        if (text != null && text != this.Problem)
            Core.DebugLog.Current?.Line("BMR", text);
        this.Problem = text;
    }

    /// <summary>1つの設定を入れる（控えを取る・外から変えられていたら入れ直す）。入ったと読み直せたら true。</summary>
    private bool Set(string key, string value)
    {
        var current = this.bmr.Read(key);
        if (current == null)
            return false;

        var i = this.lease.FindIndex(e => e.Key == key);
        if (i < 0)
        {
            this.lease.Add((key, current, value));
        }
        else
        {
            if (!Same(current, this.lease[i].Applied))
            {
                this.ExternalChanges++;
                Core.DebugLog.Current?.Line("BMR", $"BMR の {key} が外から変えられました（{this.lease[i].Applied} → {current}）。入れ直します");
            }

            this.lease[i] = (key, this.lease[i].Before, value);
        }

        if (Same(current, value))
            return true;
        return this.bmr.Write(key, value, save: false) && Same(this.bmr.Read(key), value);
    }

    private void Forget()
    {
        this.lease.Clear();
        this.aiOnByUs = false;
        this.masterBefore = null;
        this.presetBefore = null;
        this.modifiedAtLease = null;
        this.Leased = false;
        this.Combat = false;
        this.YieldMovement = false;
        this.Problem = null;
        this.lastCombat = null;
        this.lastMelee = null;
    }
}
