using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AutoMobFarm.Core;
using AutoMobFarm.Data;
using AutoMobFarm.Farm;
using AutoMobFarm.Game;
using AutoMobFarm.Ipc;
using AutoMobFarm.Judge;
using AutoMobFarm.Observe;
using AutoMobFarm.Ui;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using EstellUtils.UI;

namespace AutoMobFarm;

/// <summary>プラグインの部品のまとめ（画面と毎フレームの処理が使う）。</summary>
public sealed class PluginParts
{
    public required Configuration Config { get; init; }

    public required string ConfigDir { get; init; }

    public required string PluginVersion { get; init; }

    public required FoundSpotStore Found { get; init; }

    public required SeenMobStore Seen { get; init; }

    public required DropLog Drops { get; init; }

    public required GameWorld World { get; init; }

    public required VnavmeshIpc Nav { get; init; }

    public required RotationSolverIpc Rsr { get; init; }

    /// <summary>RSR の控え・抑止・攻撃の許可・復元。</summary>
    public required RsrController RsrControl { get; init; }

    public required LifestreamIpc Lifestream { get; init; }

    public required ITravel Travel { get; init; }

    public required FarmRunner Runner { get; init; }

    /// <summary>モブ用カタログ（3タブの画面の元データ。読めなければ null）。</summary>
    public MobCatalog? Catalog { get; init; }

    public string? CatalogError { get; init; }

    /// <summary>自動討伐のプリセットと、単品の追加先（「個別追加」）の決まり。</summary>
    public required PresetBook Presets { get; init; }

    /// <summary>複数品の収集（品ごとに FarmRunner を使う）。</summary>
    public required GoalRunner Goals { get; init; }

    /// <summary>BossMod Reborn への窓口（画面の環境の表示用）。</summary>
    public required BossModIpc BossMod { get; init; }

    /// <summary>BMR の AI に戦う段の足（範囲攻撃の回避・相手へ寄る）を任せる。</summary>
    public required BmrAssist Bmr { get; init; }
}

/// <summary>
/// カタログの狩り場で通常フィールドの素材を集める。攻撃してきた敵には反撃する。
///
/// 流れ：素材と目標数を選ぶ → カタログの地点へ移動 → 範囲と個体の判定に合う相手を倒して集める。
/// 座標クリックではその場の討伐を始める。管理画面は環境・記録を表示する。
/// 他プラグインの本体には手を加えない。vnavmesh・RSR・Lifestream・BMR の公開された IPC を使う（リフレクションは、RSR の今のモードの読み取りと、
/// IPC の無い BMR の AI の入り切り〔AIManager.SwitchToFollow・SwitchToIdle〕だけ）。
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/amf";

    private readonly PluginParts parts;
    private readonly MainWindow window;
    private readonly FarmWindow farmWindow;
    private string? lastError;
    private DateTime lastErrorAt = DateTime.MinValue;
    private readonly IReadOnlySet<uint> seenTerritories;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Svc>();

        Configuration? loaded = null;
        string? configProblem = null;
        try
        {
            loaded = Svc.PluginInterface.GetPluginConfig() as Configuration;
        }
        catch (Exception ex)
        {
            // 壊れた設定は、既定の値で上書きされる前に写しを残す（プリセットを黙って失わない）
            configProblem = BackupConfig("broken", $"設定を読めなかったので既定の値で始めます（{ex.GetType().Name}: {ex.Message}）");
            Svc.Log.Warning(ex, $"[AutoMobFarm] {configProblem}");
        }

        var config = loaded ?? new Configuration();
        var dir = Svc.PluginInterface.ConfigDirectory.FullName;
        DebugLog.Current = new DebugLog(Path.Combine(dir, "logs"));
        if (configProblem != null)
            DebugLog.Current.Line("起動", configProblem);

        var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "?";
        var buildTime = typeof(Plugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTime")?.Value ?? "?";

        var found = new FoundSpotStore(Path.Combine(dir, "found_spots.jsonl"));
        foreach (var problem in found.LoadProblems) DebugLog.Current.Line("起動", problem);
        var seen = new SeenMobStore(Path.Combine(dir, "seen_mobs.jsonl"));
        foreach (var problem in seen.LoadProblems)
            DebugLog.Current.Line("起動", problem);

        var world = new GameWorld();
        var nav = new VnavmeshIpc();
        var rsr = new RotationSolverIpc();
        var rsrControl = new RsrController(rsr, config);
        if (rsrControl.Phase == RsrPhase.Held)
            DebugLog.Current.Line("起動", rsrControl.Note ?? "前回の RSR の控えが残っています");
        var lifestream = new LifestreamIpc();
        var travel = new GameTravel(lifestream);
        var runner = new FarmRunner(world, nav, rsrControl, travel, found, new FarmLimits { NoProgress = TimeSpan.FromMinutes(Math.Max(1, config.NoProgressMinutes)) });

        MobCatalog? catalog = null;
        string? catalogError = null;
        try
        {
            catalog = MobCatalog.LoadEmbedded();
        }
        catch (Exception ex)
        {
            catalogError = $"モブ用カタログを読めません: {ex.Message}";
            DebugLog.Current.Line("起動", catalogError);
        }

        // 採集で採れる品は敵を倒して集めない。
        // 判定はいまのゲームの表で行い（GatherableItems）、一覧・品の選択・Artisan の取り込み・収集の計画が使うカタログの写しから外す。
        // 表を読めないとき（型の欄の名前が変わったなど）は外さずに続け、理由を記録に残す
        var gatherable = new HashSet<uint>();
        try
        {
            gatherable = GatherableItems.FromGame();
        }
        catch (Exception ex)
        {
            DebugLog.Current.Line("起動", $"採集で採れる品を調べられなかったので、外さずに続けます（{ex.GetType().Name}: {ex.Message}）");
        }

        if (catalog != null && gatherable.Count > 0)
        {
            catalog = catalog.WithoutItems(gatherable);
            DebugLog.Current.Line("起動", $"採集で採れる品 {catalog.RemovedItems.Count} 品（{string.Join("・", catalog.RemovedItems.Select(GameText.Item))}）と、"
                                         + $"それしか落とさない敵 {catalog.RemovedMobs.Count} 種を一覧から外しました（ゲームの採集で採れる品 {gatherable.Count} 品から判定）");
        }

        this.seenTerritories = catalog?.Territories.Select(t => t.Territory).ToHashSet() ?? [];
        MigratePresets(config, catalog);
        foreach (var note in PresetBook.Sanitize(config.Presets))
            DebugLog.Current.Line("起動", $"プリセットの読み込み：{note}");
        RemoveGatherableGoals(config, gatherable);
        var presets = new PresetBook(config.Presets, config.SelectedPresetId, config.SinglePresetId);
        var goals = new GoalRunner(runner, world, (n, t) => catalog?.PointsOf(n).Where(x => x.Territory == t).ToList() ?? [], found, item => catalog?.MobsDropping(item) is { Count: > 0 } l ? l.ToHashSet() : null, GameText.Item);
        var bossMod = new BossModIpc();
        var bmr = new BmrAssist(bossMod);
        runner.YieldMovement = () => bmr.YieldMovement; // 戦う段で BMR が動かしている間は、こちらの近づく移動を控える

        this.parts = new PluginParts
        {
            Config = config,
            ConfigDir = dir,
            PluginVersion = $"{version}（{buildTime}）",
            Found = found,
            Seen = seen,
            Drops = new DropLog(Path.Combine(dir, "drops", "drop_obs.jsonl"), GameWorld.ReadInventoryMap, version),
            World = world,
            Nav = nav,
            Rsr = rsr,
            RsrControl = rsrControl,
            Lifestream = lifestream,
            Travel = travel,
            Runner = runner,
            Catalog = catalog,
            CatalogError = catalogError,
            Presets = presets,
            Goals = goals,
            BossMod = bossMod,
            Bmr = bmr,
        };

        // 画面は UI ライブラリ EstellUtils で描く。描画の接続（UiBuilder.Draw）と
        // プラグイン一覧の「開く」（素材収集の窓）・歯車（管理画面）への接続は EstellUtils が行い、EUi.Shutdown() が外す
        EUi.Initialize(Svc.PluginInterface, log: Svc.Log);
        this.window = new MainWindow(this.parts);
        this.farmWindow = new FarmWindow(this.parts, () => this.window.IsOpen = true);
        EUi.Windows.Add(this.farmWindow, mainUi: true);
        EUi.Windows.Add(this.window, configUi: true);
        EUi.Windows.BindLayout(config.WindowLayout, config.Save);

        Svc.Commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "素材収集の画面（モブ・アイテム・自動討伐）を開きます。\n/amf admin で管理画面（環境・記録）。\n/amf stop で止めます（RSR は Off のまま。元に戻すのは画面のボタン）。",
        });

        Svc.Framework.Update += this.OnUpdate;
        Svc.ClientState.Logout += this.OnLogout;

        DebugLog.Current.Line("起動", $"読み込みました（{this.parts.PluginVersion}・カタログ {catalog?.Meta.CatalogSchema ?? "なし"}・見つけた位置 {found.Count} 件・プリセット {config.Presets.Count} 件）");
        DebugLog.Current.Line("起動", $"見かけた敵の記録：{seen.Count} 件");
        if (config.OpenOnStartup)
            this.farmWindow.IsOpen = true;
    }

    /// <summary>設定ファイルの写しを残す（壊れた設定・移行の前）。写しの場所を含む文を返す。</summary>
    private static string BackupConfig(string tag, string why)
    {
        try
        {
            var f = Svc.PluginInterface.ConfigFile;
            if (!f.Exists)
                return $"{why}（設定ファイルが無いので写しは作っていません）";
            var copy = $"{f.FullName}.{tag}-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(f.FullName, copy, overwrite: false);
            return $"{why}。元の設定は {copy} に写しました";
        }
        catch (Exception ex)
        {
            return $"{why}（写しを作れませんでした：{ex.Message}）";
        }
    }

    /// <summary>
    /// プリセットの設定の移行（版ごとに1回だけ。PresetMigration）。移行の前に設定ファイルの写しを作る。
    /// 版 1：旧版（単品の設定）からプリセットへ。単品の設定は消さずに残し、選んでいた素材があれば、その品名のプリセットを1つ作る。
    /// 版 2：帯のプリセットの名前を、アイテムタブの帯と同じ形へ（「61Lv～70Lv」→「Lv61～Lv70」）。
    /// </summary>
    private static void MigratePresets(Configuration config, MobCatalog? catalog)
    {
        if (config.PresetsVersion >= PresetMigration.CurrentVersion)
            return;
        var backup = BackupConfig($"before-presets-v{PresetMigration.CurrentVersion}", $"プリセットの設定を版 {config.PresetsVersion} から {PresetMigration.CurrentVersion} へ移行します");
        var note = PresetMigration.Migrate(config, catalog, GameText.Item);
        config.Save();
        DebugLog.Current?.Line("起動", $"{backup}。{note}");
    }

    /// <summary>
    /// 採集で採れる品をプリセットから外す（起動のたび）。外す品があるときだけ、先に設定ファイルの写しを作る。
    /// 品が無くなったプリセットも残す（名前と並びは利用者の物）。
    /// </summary>
    private static void RemoveGatherableGoals(Configuration config, IReadOnlySet<uint> gatherable)
    {
        if (gatherable.Count == 0 || !config.Presets.Any(p => p.Goals.Any(g => gatherable.Contains(g.ItemId))))
            return;
        var backup = BackupConfig("before-gatherable-removal", "採集で採れる品をプリセットから外します");
        var removed = PresetBook.RemoveItems(config.Presets, gatherable);
        config.Save();
        DebugLog.Current?.Line("起動", $"{backup}。" + string.Join("・", removed.Select(r => $"「{r.Preset.Name}」から {string.Join("・", r.Items.Select(GameText.Item))}")));
    }

    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        try
        {
            OwnPath.Frame++;
            var p = this.parts;
            p.World.Refresh();
            var nowLocal = DateTimeOffset.Now;

            p.Runner.Tick();
            p.Goals.Tick(); // 単品の収集の結果を見て、次の品へ進む（同じフレームのうちに）

            // BMR：収集中は設定を預かり、戦う段（降りて狙いの敵にハードターゲットを置いた後）だけ足を任せる。
            // 収集を終えた・止めた・コンテンツ参加中は手放す（元の設定へ戻す）
            var bmrActive = (p.Runner.Running || p.Goals.Active) && !p.Runner.InDutyStandby;
            var bmrCombat = bmrActive && p.Runner.InAttackPhase && !p.World.Mounted && !p.World.Flying;
            p.Bmr.Tick(p.World.Now, bmrActive, bmrCombat, p.World.MeleeRole != false, p.World.MyPosition);

            // RSR は状態機械の判断の後に進める（同じフレームの Suppress・Engage を、このフレームのうちに送る）。止まっている間も、復元と保持を確かめ続ける
            p.RsrControl.Tick(p.World.Now);
            p.Seen.Tick(p.World.Now, p.Config.SeenMobRecording, p.Runner.Running, p.Runner.State == FarmState.Standby,
                p.World.Player, p.World.Mobs, this.seenTerritories);
            if (p.Runner.Running)
                p.Drops.Tick(p.World.Mobs, p.World.Player, GameWorld.OtherItemSourceActive, p.World.Now, nowLocal);
        }
        catch (Exception ex)
        {
            // 同じ例外が毎フレーム出ても記録を埋めない（文言が変わったときか10秒おきにだけ全文を書く）
            var text = $"{ex.GetType().Name}: {ex.Message}";
            if (text != this.lastError || DateTime.UtcNow - this.lastErrorAt > TimeSpan.FromSeconds(10))
            {
                this.lastError = text;
                this.lastErrorAt = DateTime.UtcNow;
                Svc.Log.Error($"[AutoMobFarm] 処理中に例外が出ました: {ex}");
                DebugLog.Current?.Line("例外", ex.ToString());
            }

            try
            {
                this.HoldEverything("exception", $"処理中に例外が出たので止めました（{ex.GetType().Name}: {ex.Message}）。RSR は Off のまま控えを保持しています");
            }
            catch
            {
                // 止める途中の例外は次のフレームで
            }
        }
    }

    /// <summary>
    /// 安全側で全部止める（例外・ログアウト・読み込みの解除・/amf stop）：その場の討伐・収集（複数品を含む）の移動と狙いを解き、
    /// RSR は Off のまま控えを保持する（元に戻すのは画面の「RSR を元に戻す」）。
    /// </summary>
    private void HoldEverything(string code, string reason)
    {
        var p = this.parts;
        if (p.Goals.Active)
            p.Goals.StopAll(reason, restore: false);
        else
            p.Runner.HoldStop(code, reason);
    }

    private void OnLogout(int type, int code)
    {
        this.HoldEverything("logout", "ログアウトしました。RSR は Off のまま控えを保持しています");
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim())
        {
            case "":
                this.farmWindow.IsOpen = !this.farmWindow.IsOpen;
                break;
            case "admin":
                this.window.IsOpen = !this.window.IsOpen;
                break;
            case "stop":
                // 緊急の停止として使われるので、RSR は Off のまま控えを保持する（元に戻すのは画面の「RSR を元に戻す」）
                this.HoldEverything("user-hold", "コマンド /amf stop（RSR は Off のまま控えを保持）");
                break;
            default:
                Svc.Chat.Print($"使い方: {CommandName}（素材収集の画面） / {CommandName} admin（管理画面） / {CommandName} stop（止める）");
                break;
        }
    }

    public void Dispose()
    {
        Svc.Framework.Update -= this.OnUpdate;
        Svc.ClientState.Logout -= this.OnLogout;
        Svc.Commands.RemoveHandler(CommandName);
        // 描画とプラグイン一覧のボタンへの接続を外し、窓の登録を消す
        EUi.Shutdown();

        // 読み込みの解除：自分の経路探索を取り消し、自分の移動と戦闘連携（ハードターゲット・RSR）を解く。他プラグインの移動は止めない
        try
        {
            // 解除の後は毎フレームの処理が無く復元を確かめられないので、Off のまま控えを保持する（次に読み込んだとき「RSR を元に戻す」で戻せる）
            this.HoldEverything("unload", "プラグインの読み込みを解除しました。RSR は Off のまま控えを保持しています");

            // BMR は預かった設定を元へ戻す（解除の後は戻す機会が無い。BMR の設定ファイルへは書いていない）
            this.parts.Bmr.Release("プラグインの読み込みを解除");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[AutoMobFarm] 後片づけで例外: {ex.Message}");
        }
    }
}
