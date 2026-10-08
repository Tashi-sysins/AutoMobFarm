using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoMobFarm.Farm;

/// <summary>RSR の扱いの段階。</summary>
public enum RsrPhase
{
    /// <summary>触っていない（控えも無い）。</summary>
    Idle,

    /// <summary>制御中（控えを取り、Off で攻撃を抑止するか、確かめたうえで Henched にする）。</summary>
    Controlling,

    /// <summary>元の設定へ戻している（読み直して確かめるまで）。</summary>
    Restoring,

    /// <summary>安全停止：Off のまま控えを保持（利用者が「元に戻す」を押すまで戻さない）。</summary>
    Held,
}

/// <summary>RSR の元の設定の控え。</summary>
public sealed record RsrBackup(byte Mode, byte Aoe, IReadOnlyDictionary<string, bool> Bools);

/// <summary>
/// RSR の制御（Henched は無攻撃のモードではない：RSR の RSCommands_StateSpecialCommand.cs で
/// State・IsManual・IsHenched が立ち、ハードターゲットを殴る）。次の4つを別々に持つ：
///  1. 元の設定の控え（モード・範囲攻撃・真偽の設定。設定ファイルにも残す）
///  2. 今の攻撃の可否：既定は「抑止」＝モード Off。攻撃を許すのは呼び出し側が Engage を頼み、かつ範囲攻撃 Off・TargetFreely false・
///     IgnoreNonFateInFate false・外部ターゲット指定なしを読み直して確かめた後に Henched を送り、Henched を読み直したときだけ（<see cref="AttackReady"/>）。
///  3. 設定の反映の確認：送った（受け付けた）ことと、読み直して期待値だったことを分ける。読めない・送っても反映しない・一度合った値が外から変わった、
///     は <see cref="Problem"/> になる（確かめるまで攻撃させない（止めない））。
///  4. 復元待ち：通常の停止では控えの値へ戻し、読み直して確かめるまで控えを捨てない。安全停止（<see cref="Hold"/>）では Off のまま控えを保持し、
///     元の Auto などへ自動では戻さない（戻すと対象外を殴りうる）。利用者が「元に戻す」を押したときだけ戻す。
/// 利用者が自分で RSR を切り替えた（保持中・復元中に、こちらの送った値でも元の値でもない値になった）ら、上書きせずに手放す。
/// </summary>
public sealed class RsrController : ICombatPermit
{
    public const byte ModeOff = 0;
    public const byte ModeHenched = 5;
    public const byte AoeOff = 0;

    /// <summary>Henched の間だけでなく、制御中はずっと false にしておく真偽の設定（狙いが空になると近くの敵を自分で狙う・FATE の内外で狙いを変える）。</summary>
    public static readonly string[] FalseSettings = ["TargetFreely", "IgnoreNonFateInFate"];

    public static readonly TimeSpan ReadInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(1);

    /// <summary>読んだ値をこれより古ければ「確かめた」に数えない。</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(1.5);

    /// <summary>読めない状態がこれだけ続いたら問題にする。</summary>
    public static readonly TimeSpan ReadFailLimit = TimeSpan.FromSeconds(3);

    /// <summary>同じ値を送り直す回数の上限（超えたら「反映しない」）。</summary>
    public const int ResendLimit = 5;

    /// <summary>元へ戻したと確かめるまでの上限。</summary>
    public static readonly TimeSpan RestoreLimit = TimeSpan.FromSeconds(15);

    private readonly IRotationControl rsr;
    private readonly IRsrStore? store;

    // 読んだ値（読めなければ null）と、読んだ時刻
    private byte? mode;
    private byte? aoe;
    private readonly Dictionary<string, bool?> bools = [];
    private bool? targetOverride;
    private DateTime readAt = DateTime.MinValue;
    private DateTime? readFailSince;

    // 送った記録（項目ごと：送った値・回数・時刻）と、一度期待値になった項目
    private readonly Dictionary<string, (string Value, int Count, DateTime At)> sent = [];
    private readonly Dictionary<string, string> reached = [];
    private DateTime restoreSince;
    private readonly Dictionary<string, string> stuck = [];
    private DateTime lastOverrideClear = DateTime.MinValue;
    public static readonly TimeSpan SlowResendInterval = TimeSpan.FromSeconds(5);

    public RsrController(IRotationControl rsr, IRsrStore? store)
    {
        this.rsr = rsr;
        this.store = store;

        // 前回の控えが残っていれば（読み込み直し・落ちた後）、保持として引き継ぐ。黙って捨てず、自動では戻さない
        if (store?.RsrHenchedPending == true && store.RsrOriginalMode is { } m && store.RsrAoeOriginal is { } a)
        {
            this.Backup = new RsrBackup(m, a, new Dictionary<string, bool>(store.RsrBoolOriginals));
            this.Phase = RsrPhase.Held;
            this.Note = "前回の RSR の元の設定の控えが残っています（「RSR を元に戻す」で戻します）";
        }
    }

    public RsrPhase Phase { get; private set; } = RsrPhase.Idle;

    public RsrBackup? Backup { get; private set; }

    /// <summary>攻撃を頼まれているか（Engage）。既定は抑止。</summary>
    public bool WantEngage { get; private set; }

    /// <summary>
    /// いまの警告（読めない・送っても反映しない・外部ターゲット指定が有効・控えを取れない）。無ければ null。毎回計算し直す（溜めない）。
    /// 止める理由には使わない（D10）。確かめられない間は SettingsVerified・AttackReady が false なので、攻撃は許さない。
    /// </summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// 制御中に、一度合った値が外から変わった（利用者・他のプラグイン・RSR 自身の自動 OFF が変えた）ので送り直した回数（画面・記録・試験用）。
    /// 実行をまたいで数える（Take で 0 に戻さない）。
    /// </summary>
    public int ExternalChanges { get; private set; }

    /// <summary>元へ戻せたと確かめられなかった理由（無ければ null）。</summary>
    public string? RestoreProblem { get; private set; }

    /// <summary>画面に出す説明（保持中・手放した等）。</summary>
    public string? Note { get; private set; }

    public bool Controlling => this.Phase == RsrPhase.Controlling;

    /// <summary>RSR が読み込まれているか。</summary>
    public bool IsRsrLoaded => this.rsr.IsLoaded;

    /// <summary>読んだ値が新しいか。</summary>
    public bool FreshAt(DateTime now) => now - this.readAt <= Fresh && this.readFailSince == null;

    private DateTime lastNow;

    /// <summary>範囲攻撃 Off・真偽の設定 false・外部ターゲット指定なしを読み直して確かめたか。</summary>
    public bool SettingsVerified => this.FreshAt(this.lastNow) && this.aoe == AoeOff && FalseSettings.All(n => this.bools.GetValueOrDefault(n) == false) && this.targetOverride == false;

    /// <summary>保持中なのに、Henched のままだと読めているか（Off の命令を断る・反映しない）。読めないときは false（決めつけない）。</summary>
    public bool HeldButHenched => this.Phase == RsrPhase.Held && this.FreshAt(this.lastNow) && this.mode == ModeHenched;

    /// <summary>制御中で、モード Off を読み直して確かめたか（攻撃の抑止）。</summary>
    public bool SuppressedVerified => this.Controlling && this.FreshAt(this.lastNow) && this.mode == ModeOff;

    /// <summary>攻撃してよい（Engage を頼まれ、設定を確かめ、Henched を読み直した）。</summary>
    public bool AttackReady => this.Controlling && this.WantEngage && this.SettingsVerified && this.mode == ModeHenched;

    /// <summary>今読んでいるモード（画面用）。</summary>
    public byte? CurrentMode => this.mode;

    public byte? CurrentAoe => this.aoe;

    /// <summary>
    /// 制御を始める（控えを取る）。読めなければ false（問題に理由）。前回の控え（保持中）があれば、それを使う前の設定として引き継ぐ
    /// （いまの Off を使う前の設定と取り違えない）。
    /// </summary>
    public bool Take(DateTime now)
    {
        this.lastNow = now;
        this.Problem = null;
        this.stuck.Clear();
        this.RestoreProblem = null;
        this.sent.Clear();
        this.reached.Clear();
        this.WantEngage = false;
        if (!this.rsr.IsLoaded)
        {
            this.Problem = "RotationSolverReborn が読み込まれていません";
            return false;
        }

        this.Read(now, force: true);
        if (this.Backup == null)
        {
            if (this.mode is not { } m || this.aoe is not { } a || FalseSettings.Any(n => this.bools.GetValueOrDefault(n) == null))
            {
                this.Problem = $"RSR の今の設定を読めないので、元の設定の控えを取れません（{this.rsr.LastReadError}）";
                return false;
            }

            this.Backup = new RsrBackup(m, a, FalseSettings.ToDictionary(n => n, n => this.bools[n]!.Value));
            this.SaveBackup(held: false);
            Core.DebugLog.Current?.Line("RSR", $"元の設定を控えました：モード {ModeName(m)}・範囲攻撃 {AoeName(a)}・{string.Join("・", this.Backup.Bools.Select(kv => $"{kv.Key} {kv.Value}"))}");
        }
        else
        {
            this.SaveBackup(held: false);
            Core.DebugLog.Current?.Line("RSR", "前回の控えを使う前の設定として引き継ぎます");
        }

        this.Phase = RsrPhase.Controlling;
        this.Note = null;
        return true;
    }

    /// <summary>攻撃を抑止する（モード Off を目指す）。</summary>
    public void Suppress() => this.WantEngage = false;

    /// <summary>攻撃を頼む（設定を確かめてから Henched を送る）。</summary>
    public void Engage() => this.WantEngage = true;

    /// <summary>
    /// 安全停止：Off のまま控えを保持する（自動では戻さない）。制御中でなければ何もしない。
    /// </summary>
    public void Hold(string why)
    {
        if (this.Phase is RsrPhase.Idle)
            return;
        this.WantEngage = false;
        this.Phase = RsrPhase.Held;

        // 送った後にまだ読み直していなければ、手元の値は古い（Off と読めていても Henched を送った直後かもしれない）。
        // 「Off の命令がまだ効いていないだけ」とみなすのは最後に読めた値（制御中にこちらが送るのは Off か Henched だけで、Henched は TickHeld が別に扱う）
        var unread = this.sent.TryGetValue("mode", out var lastSent) && lastSent.At >= this.readAt;
        this.heldFrom = this.mode;
        this.sent.Clear();
        this.reached.Clear();

        // 次のフレームが来ない（読み込みの解除・ログアウト）こともあるので、Off と確かめられていなければ Off の命令をすぐ1回送る（以後は TickHeld が確かめる）
        if (this.mode != ModeOff || unread)
        {
            var ok = this.SendMode(ModeOff);
            this.sent["mode"] = (ModeName(ModeOff), 1, this.lastNow);
            Core.DebugLog.Current?.Line("RSR", $"保持に入るので動作モードを Off にする命令を送りました（{(ok ? "受け付け" : "送れなかった")}）");
        }

        this.SaveBackup(held: true);
        this.Note = $"RSR は Off のまま止めています（{why}）。元の設定へ戻すには「RSR を元に戻す」を押してください";
        Core.DebugLog.Current?.Line("RSR", this.Note);
    }

    /// <summary>元の設定へ戻し始める（通常の停止・利用者の「元に戻す」）。読み直して確かめるまで控えを残す。</summary>
    public void Restore(DateTime now)
    {
        if (this.Backup == null)
        {
            this.Phase = RsrPhase.Idle;
            return;
        }

        this.WantEngage = false;
        this.Phase = RsrPhase.Restoring;
        this.restoreSince = now;
        // 制御開始直後はキャッシュが元の値でも、送った Off は既に反映していることがある。
        this.Read(now, force: true);
        this.sent.Clear();
        this.RestoreProblem = null;
        this.Note = "RSR を元の設定へ戻しています";
    }

    /// <summary>1フレーム進める（読む・必要なら送る）。</summary>
    public void Tick(DateTime now)
    {
        this.lastNow = now;
        if (this.Phase == RsrPhase.Idle)
            return;

        this.Read(now, force: false);
        switch (this.Phase)
        {
            case RsrPhase.Controlling:
                this.TickControl(now);
                break;
            case RsrPhase.Restoring:
                this.TickRestore(now);
                break;
            case RsrPhase.Held:
                this.TickHeld(now);
                break;
        }
    }

    private void Read(DateTime now, bool force)
    {
        if (!force && now - this.readAt < ReadInterval)
            return;
        this.mode = this.rsr.ReadMode();
        this.aoe = this.rsr.ReadAoe();
        foreach (var n in FalseSettings)
            this.bools[n] = this.rsr.ReadBool(n);
        this.targetOverride = this.rsr.ReadTargetFreelyOverride();
        var ok = this.mode != null && this.aoe != null && this.targetOverride != null && FalseSettings.All(n => this.bools[n] != null);
        if (ok)
        {
            this.readFailSince = null;
            this.readAt = now;
        }
        else
        {
            this.readFailSince ??= now;
        }
    }

    private void TickControl(DateTime now)
    {
        string? warn = null;
        if (this.readFailSince is { } since && now - since > ReadFailLimit)
            warn = $"RSR の状態を {ReadFailLimit.TotalSeconds:0}秒読めません（{this.rsr.LastReadError}）。読めるまで攻撃させません";
        if (this.targetOverride == true)
        {
            warn ??= "RSR の外部ターゲット指定が有効です。解除を読み直すまで攻撃させません";
            if (now - this.lastOverrideClear >= ResendInterval)
            {
                this.lastOverrideClear = now;
                var ok = this.rsr.DisableTargetFreelyOverride();
                Core.DebugLog.Current?.Line("RSR", $"外部ターゲット指定の解除（DisableTargetFreelyOverride）を頼みました（{ok}）");
            }
        }
        if (this.FreshAt(now))
        {
            this.Want("aoe", this.aoe == AoeOff, AoeName(AoeOff), () => this.rsr.SendAoe(AoeOff), now);
            foreach (var n in FalseSettings) this.Want(n, this.bools[n] == false, "false", () => this.rsr.SendBool(n, false), now);
            var wantMode = this.WantEngage && this.SettingsVerified ? ModeHenched : ModeOff;
            this.Want("mode", this.mode == wantMode, ModeName(wantMode), () => this.SendMode(wantMode), now);
        }
        this.Problem = warn ?? this.stuck.Values.FirstOrDefault();
    }

    /// <summary>
    /// ある項目を期待値にする。合っていれば「一度合った」を記録。合わなければ送る（ResendLimit 回までは ResendInterval ごと、その後は
    /// SlowResendInterval ごとに送り続け、警告を出す）。一度合った値が外から変わったら、数えて送り直す（止めない：D10）。
    /// </summary>
    private void Want(string key, bool matches, string desired, Func<bool> send, DateTime now)
    {
        // 送った後にまだ読み直していなければ、手元の値は送る前の古い値なので、それで「合った」「外から変わった」を決めない。
        // 期待値が送った値と同じなら待ち、変わっていれば（Henched を頼んだ直後に抑止に戻った等）すぐ送り直す
        if (this.sent.TryGetValue(key, out var last) && last.At >= this.readAt)
        {
            if (last.Value == desired)
                return;
            var ok2 = send();
            this.sent[key] = (desired, 1, now);
            this.stuck.Remove(key); // 前の期待値の「反映しません」は、新しい期待値には当てはまらない
            Core.DebugLog.Current?.Line("RSR", $"{KeyName(key)} を {desired} にする命令を送りました（期待値が変わったので読み直しを待たずに・{(ok2 ? "受け付け" : "送れなかった")}）");
            return;
        }

        if (matches)
        {
            this.reached[key] = desired;
            this.stuck.Remove(key);
            return;
        }

        if (this.reached.TryGetValue(key, out var had) && had == desired)
        {
            // 一度期待値になった後に、こちらが期待値を変えていないのに変わった＝外からの変更（利用者・他のプラグイン・RSR 自身の自動 OFF）
            this.ExternalChanges++;
            this.reached.Remove(key);
            this.sent.Remove(key);
            Core.DebugLog.Current?.Line("RSR", $"RSR の {KeyName(key)} が外から変わりました（期待 {desired}）。送り直します");
        }

        if (this.sent.TryGetValue(key, out var s) && s.Value == desired)
        {
            if (s.Count >= ResendLimit)
                this.stuck[key] = $"RSR の {KeyName(key)} を {desired} にする命令を {s.Count} 回送っても反映しません。{SlowResendInterval.TotalSeconds:0}秒おきに送り続けます";
            if (now - s.At < (s.Count >= ResendLimit ? SlowResendInterval : ResendInterval))
                return;
        }

        var count = this.sent.TryGetValue(key, out var prev) && prev.Value == desired ? prev.Count + 1 : 1;
        if (count == 1)
            this.stuck.Remove(key); // 期待値が変わった（または送り直しを数え直した）。前の期待値の「反映しません」は消す
        var ok = send();
        this.sent[key] = (desired, count, now);
        this.reached.Remove(key); // 期待値を自分で変えたので、前に合っていた記録は「外からの変更」の判定に使わない（送った直後の分岐でもここを通った後なので消えている）
        Core.DebugLog.Current?.Line("RSR", $"{KeyName(key)} を {desired} にする命令を送りました（{count} 回目・{(ok ? "受け付け" : "送れなかった")}）");
    }

    private void TickRestore(DateTime now)
    {
        if (!this.FreshAt(now) || this.Backup is not { } b)
        {
            if (now - this.restoreSince > RestoreLimit)
                this.FailRestore("RSR の状態を読めない");
            return;
        }

        var done = true;
        done &= this.RestoreOne("aoe", this.aoe, b.Aoe, AoeOff, v => this.rsr.SendAoe(v), now);
        foreach (var n in FalseSettings)
        {
            var cur = this.bools[n];
            var orig = b.Bools.GetValueOrDefault(n, true);
            if (cur == orig)
                continue;
            if (cur == false)
            {
                done = false;
                this.SendThrottled(n, orig ? "true" : "false", () => this.rsr.SendBool(n, orig), now);
            }
        }

        // モード：こちらが置いた値（Off・Henched）のときだけ戻す。それ以外なら利用者が切り替えたので上書きしない
        if (this.mode != b.Mode && this.mode is ModeOff or ModeHenched)
        {
            done = false;
            this.SendThrottled("mode", ModeName(b.Mode), () => this.SendMode(b.Mode), now);
        }

        if (done)
        {
            Core.DebugLog.Current?.Line("RSR", $"RSR が元の設定（モード {ModeName(b.Mode)}・範囲攻撃 {AoeName(b.Aoe)}）に戻ったことを読み直して確かめました");
            this.Forget("RSR を元の設定へ戻しました");
            return;
        }

        if (now - this.restoreSince > RestoreLimit)
            this.FailRestore("送っても元の値にならない");
    }

    private bool RestoreOne(string key, byte? cur, byte orig, byte ours, Func<byte, bool> send, DateTime now)
    {
        if (cur == orig)
            return true;
        if (cur != ours)
            return true; // こちらが置いた値ではない＝利用者が変えた。上書きしない
        this.SendThrottled(key, orig.ToString(), () => send(orig), now);
        return false;
    }

    private void SendThrottled(string key, string value, Func<bool> send, DateTime now)
    {
        if (this.sent.TryGetValue(key, out var s) && s.Value == value && now - s.At < ResendInterval)
            return;
        var count = this.sent.TryGetValue(key, out var prev) && prev.Value == value ? prev.Count + 1 : 1;
        var ok = send();
        this.sent[key] = (value, count, now);
        Core.DebugLog.Current?.Line("RSR", $"{KeyName(key)} を {value} にする命令を送りました（{(this.Phase == RsrPhase.Held ? "保持" : "元の設定へ戻す")}・{count} 回目・{(ok ? "受け付け" : "送れなかった")}）");
    }

    private void FailRestore(string why)
    {
        this.RestoreProblem = $"RSR を元の設定へ戻せたと確かめられません（{why}）。控えは残しています。もう一度「RSR を元に戻す」を押すか、RSR の画面で確かめてください";
        this.Phase = RsrPhase.Held;
        this.SaveBackup(held: true);
        Core.DebugLog.Current?.Line("RSR", this.RestoreProblem);
    }

    // 保持に入ったときに読めていたモード（「まだ Off が効いていないだけ」の値）。Off の命令がまだ効いていないのか、利用者が切り替えたのかを分ける
    private byte? heldFrom;

    private bool SendMode(byte mode) => this.rsr.SendMode(mode);

    private void TickHeld(DateTime now)
    {
        if (!this.FreshAt(now))
            return;

        // 保持中は Off を目指す。Off になった後は送らない
        if (this.mode == ModeOff)
        {
            this.reached["held"] = "off";
            return;
        }

        // Henched はこちらが置く値なので、保持中に見えたら（Off の後に遅れて効いた Henched の命令を含む）手放さずに Off を送り直す。
        // 保持に入ったときの値のまま（まだ Off になっていない）なら、Off の命令がまだ効いていないとみなして送り直す。
        // それ以外（一度 Off になった後に Auto 等へ変わった、こちらが置かない値になった）＝利用者が自分で切り替えた → 上書きせずに手放す（控えは使わない）
        var notYetOff = !this.reached.ContainsKey("held") && this.mode == this.heldFrom;
        if (this.mode != ModeHenched && !notYetOff)
        {
            Core.DebugLog.Current?.Line("RSR", $"保持中に RSR が {ModeName(this.mode!.Value)} に切り替えられました（利用者の操作とみなし、上書きせずに手放します）");
            this.Relinquish("利用者が RSR を切り替えたので、AutoMobFarm は RSR から手を放しました（元の設定の控えは捨てました）");
            return;
        }

        // Off にならないまま続く（命令を断る・反映しない）なら、利用者に手動で Off にしてもらう（こちらからはこれ以上止められない）
        if (this.sent.TryGetValue("mode", out var s) && s.Count >= ResendLimit && this.RestoreProblem == null)
        {
            this.RestoreProblem = $"RSR が Off になりません（{s.Count} 回送っても {ModeName(this.mode!.Value)} のまま）。RSR の画面で手動で Off にしてください";
            Core.DebugLog.Current?.Line("RSR", this.RestoreProblem);
        }

        this.SendThrottled("mode", ModeName(ModeOff), () => this.SendMode(ModeOff), now);
    }

    /// <summary>上書きせずに手放す（利用者が自分で RSR を操作したとき）。控えは捨てる。</summary>
    public void Relinquish(string note) => this.Forget(note);

    /// <summary>控えを捨てて手放す（戻せた・利用者が切り替えた）。</summary>
    private void Forget(string note)
    {
        this.Phase = RsrPhase.Idle;
        this.Backup = null;
        this.WantEngage = false;
        this.Problem = null;
        this.stuck.Clear();
        this.sent.Clear();
        this.reached.Clear();
        this.Note = note;
        if (this.store != null)
        {
            this.store.RsrHenchedPending = false;
            this.store.RsrHeld = false;
            this.store.RsrOriginalMode = null;
            this.store.RsrAoeOriginal = null;
            this.store.RsrBoolOriginals.Clear();
            this.store.Save();
        }
    }

    private void SaveBackup(bool held)
    {
        if (this.store == null || this.Backup is not { } b)
            return;
        this.store.RsrHenchedPending = true;
        this.store.RsrHeld = held;
        this.store.RsrOriginalMode = b.Mode;
        this.store.RsrAoeOriginal = b.Aoe;
        this.store.RsrBoolOriginals.Clear();
        foreach (var (k, v) in b.Bools)
            this.store.RsrBoolOriginals[k] = v;
        this.store.Save();
    }

    private static string KeyName(string key) => key switch
    {
        "aoe" => "範囲攻撃（AoEType）",
        "mode" => "動作モード",
        _ => key,
    };

    public static string ModeName(byte mode) => mode switch
    {
        0 => "Off",
        1 => "Auto",
        2 => "TargetOnly",
        3 => "Manual",
        4 => "AutoDuty",
        5 => "Henched",
        6 => "PvP",
        _ => $"不明({mode})",
    };

    public static string AoeName(byte value) => value switch
    {
        0 => "Off",
        1 => "Cleave",
        2 => "Full",
        _ => $"不明({value})",
    };
}
