using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;

namespace AutoMobFarm.Ui;

/// <summary>
/// AutoMobFarm が使うほかのプラグインの一覧と、いま使えるか・どの配布元から入れるかの判断（主画面の「必須プラグイン」タブ）。
/// 足りないプラグインは、ボタン 1 つで配布元の登録からインストールまで行う。足りていればボタンは出さない。
///
/// 画面にもゲームにも触らないので、ゲーム無しで試せる。Dalamud の内部に触る部分は Ipc\PluginInstaller.cs にある。
/// 見分けは InternalName（表示名は変わることがあるため使わない）。
/// </summary>
public static class RequiredPlugins
{
    /// <summary>どのくらい要るか。</summary>
    public enum Need
    {
        /// <summary>無いと収集を始められない。</summary>
        Required,

        /// <summary>無くても収集はできるが、その機能が無くなる。</summary>
        Recommended,
    }

    /// <summary>1 つのプラグイン。</summary>
    /// <param name="InternalName">Dalamud の InternalName（見分けに使う）。</param>
    /// <param name="DisplayName">画面に出す名前。配布元の一覧の名前と同じ（インストール画面をこの名前で検索する）。</param>
    /// <param name="Authors">作者（配布元の一覧の Author の表記のまま。他所の成果を紹介するので必ず添える）。</param>
    /// <param name="Need">どのくらい要るか。</param>
    /// <param name="UsedFor">AutoMobFarm が何に使うか。</param>
    /// <param name="IfMissing">無いとどうなるか。</param>
    /// <param name="RepoUrls">配布元（Dalamud の「カスタムプラグインリポジトリ」の URL）。先頭が登録する URL、ほかは同じ中身の別の書き方。</param>
    /// <param name="Note">注意書き。無ければ null。</param>
    public sealed record Entry(string InternalName, string DisplayName, string Authors, Need Need, string UsedFor, string IfMissing,
        IReadOnlyList<string> RepoUrls, string? Note = null);

    private const string CombatRebornRepo = "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json";

    /// <summary>
    /// 使うプラグインの全部（画面の並び）。
    /// InternalName は AutoMobFarm の連携の口（Ipc\*.cs）と同じ。DisplayName・Authors・配布元は各配布元の一覧を取得して、
    /// その InternalName が API 15 で載っていることを確かめた。
    /// 配布元が変わったら直す。
    /// </summary>
    public static readonly IReadOnlyList<Entry> All =
    [
        new("vnavmesh", "vnavmesh", "veyn, xan_0", Need.Required,
            "狩り場の中の移動・敵へ近づく経路",
            "収集を始められません",
            ["https://puni.sh/api/repository/veyn"]),
        new("RotationSolver", "Rotation Solver Reborn", "The Combat Reborn Team", Need.Required,
            "戦闘（技を自動で撃つ）",
            "収集を始められません（敵を倒せません）",
            [CombatRebornRepo]),
        new("Lifestream", "Lifestream", "NightmareXIV", Need.Recommended,
            "狩り場のエリアへのテレポ",
            "狩り場のエリアへは手動で移動します",
            ["https://raw.githubusercontent.com/NightmareXIV/MyDalamudPlugins/main/pluginmaster.json",
             "https://github.com/NightmareXIV/MyDalamudPlugins/raw/main/pluginmaster.json"]),
        new("BossModReborn", "BossMod Reborn", "The Combat Reborn Team", Need.Recommended,
            "範囲攻撃の回避・戦うときに相手へ寄る",
            "範囲攻撃を避けません（収集はできます）",
            [CombatRebornRepo],
            "本家の BossMod（VBM）も読み込まれていると、AutoMobFarm は BossMod Reborn を使いません（連携の名前が同じで、どちらに届くか決まらないため）。"),
    ];

    /// <summary>いまの状態。</summary>
    public enum Status
    {
        /// <summary>導入されていて、読み込まれている（使える）。</summary>
        Loaded,

        /// <summary>導入されているが、読み込まれていない（Dalamud の一覧で無効・読み込みに失敗など）。</summary>
        NotLoaded,

        /// <summary>導入されていない。</summary>
        Missing,
    }

    /// <summary>1 つのプラグインの状態。Version は導入されていれば版（無ければ null）。</summary>
    public sealed record State(Entry Entry, Status Status, string? Version);

    /// <summary>
    /// 導入済みの一覧から、全部のプラグインの状態を作る。
    /// 同じ InternalName が 2 つある（開発版と配布版を両方入れている）ときは、読み込まれている方を使う。
    /// </summary>
    public static List<State> Check(IEnumerable<(string? InternalName, bool IsLoaded, string? Version)> installed)
    {
        var list = installed.Where(p => !string.IsNullOrEmpty(p.InternalName)).ToList();
        return All.Select(entry =>
        {
            var same = list.Where(p => string.Equals(p.InternalName, entry.InternalName, StringComparison.Ordinal)).ToList();
            if (same.Count == 0)
                return new State(entry, Status.Missing, null);

            var loaded = same.Where(p => p.IsLoaded).ToList();
            return loaded.Count > 0
                ? new State(entry, Status.Loaded, loaded[0].Version)
                : new State(entry, Status.NotLoaded, same[0].Version);
        }).ToList();
    }

    /// <summary>
    /// Dalamud の導入済みの一覧から、内部名・読み込まれているか・版を読む。
    /// Dalamud は版に manifest の AssemblyVersion をそのまま返すので、版の無い manifest（開発用のプラグインなど）があると null になる。
    /// 版は null のまま扱い、ほかの値を読むときに例外が出たプラグインは飛ばす（1 つ読めなくても、ほかは読む）。
    /// </summary>
    public static List<(string? InternalName, bool IsLoaded, string? Version)> Read(IEnumerable<IExposedPlugin> installed)
    {
        var list = new List<(string? InternalName, bool IsLoaded, string? Version)>();
        foreach (var p in installed)
        {
            try
            {
                if (p is null)
                    continue;
                string? version = null;
                try { version = p.Version?.ToString(); }
                catch (Exception) { /* 版だけ読めない：版なしとして扱う */ }
                list.Add((p.InternalName, p.IsLoaded, version));
            }
            catch (Exception)
            {
                // このプラグインは読めない。ほかのプラグインは読み続ける。
            }
        }

        return list;
    }

    /// <summary>カードに出すボタン。</summary>
    public enum Button
    {
        /// <summary>出さない（使える）。</summary>
        None,

        /// <summary>インストール（配布元の登録からインストールまで 1 回で行う）。</summary>
        Install,

        /// <summary>有効にする（導入済みだが読み込まれていない）。</summary>
        Enable,
    }

    /// <summary>その状態で出すボタン。</summary>
    public static Button ButtonFor(Status status) => status switch
    {
        Status.Loaded => Button.None,
        Status.NotLoaded => Button.Enable,
        _ => Button.Install,
    };

    /// <summary>使えないプラグインの数（導入されていない・読み込まれていない）。</summary>
    public static int Unavailable(IEnumerable<State> states) => states.Count(s => s.Status != Status.Loaded);

    /// <summary>使えない「必須」のプラグイン（主画面の上に出す。無いと収集を始められないもの）。</summary>
    public static List<State> RequiredUnavailable(IEnumerable<State> states)
        => states.Where(s => s.Entry.Need == Need.Required && s.Status != Status.Loaded).ToList();

    /// <summary>状態の文。</summary>
    public static string StatusText(State state) => state.Status switch
    {
        Status.Loaded => state.Version is { Length: > 0 } v ? $"導入済み（{v}）" : "導入済み",
        Status.NotLoaded => "導入済み・停止中",
        _ => "未インストール",
    };

    // ------------------------------------------------------------------
    // 配布元
    // ------------------------------------------------------------------

    /// <summary>配布元が Dalamud の設定（カスタムプラグインリポジトリ）に入っているか。</summary>
    public enum RepoStatus
    {
        /// <summary>登録済みで有効（足さなくてよい）。</summary>
        Added,

        /// <summary>登録済みだが無効（有効に戻す。無効のままだと Dalamud は読まず、配布一覧に出てこない）。</summary>
        Disabled,

        /// <summary>登録されていない（先頭の URL を足す）。</summary>
        Missing,

        /// <summary>Dalamud の設定を読めない（登録されているか分からない）。</summary>
        Unknown,
    }

    /// <summary>
    /// そのプラグインの配布元が、Dalamud の設定のカスタムプラグインリポジトリに入っているか。
    /// repos は登録済みの配布元。null は読めなかった。同じ中身の別の URL のどれかが有効なら登録済み。
    /// 「登録されているか」だけを見ると、無効にしてある配布元を登録済みとみなし、配布一覧に出ないまま失敗する。ここでは有効かまで見る。
    /// </summary>
    public static RepoStatus RepoState(Entry entry, IReadOnlyList<(string Url, bool Enabled)>? repos)
    {
        if (repos is null)
            return RepoStatus.Unknown;
        var found = repos.Where(r => entry.RepoUrls.Any(u => SameUrl(u, r.Url))).ToList();
        if (found.Any(r => r.Enabled))
            return RepoStatus.Added;
        return found.Count > 0 ? RepoStatus.Disabled : RepoStatus.Missing;
    }

    /// <summary>URL が同じか（前後の空白・末尾の「/」・大文字小文字を見ない）。</summary>
    public static bool SameUrl(string? a, string? b)
        => a is not null && b is not null
           && string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Dalamud が配布元を読み込んだ状態。</summary>
    public enum RepoLoad
    {
        /// <summary>Dalamud の配布元の一覧に入っていない（設定を読み直していない）。</summary>
        NotListed,

        /// <summary>読み込み中・まだ読んでいない。</summary>
        Loading,

        /// <summary>読み込めた。</summary>
        Loaded,

        /// <summary>読み込めなかった（回線・配布元の停止など）。</summary>
        Failed,
    }

    /// <summary>
    /// Dalamud の配布元の一覧（PluginManager.Repos の PluginMasterUrl と State）から、そのプラグインの配布元の読み込み状態を決める。
    /// State は Dalamud の PluginRepositoryState の名前（Unknown・InProgress・Success・Fail。Dalamud 15.0.3.6 で確認）。
    /// 同じ中身の別の URL が 2 つ登録されていれば、どちらかが読めていれば読めたとする。
    /// </summary>
    public static RepoLoad RepoLoadState(Entry entry, IEnumerable<(string? Url, string? State)> repos)
    {
        var states = repos.Where(r => entry.RepoUrls.Any(u => SameUrl(u, r.Url))).Select(r => r.State).ToList();
        if (states.Count == 0)
            return RepoLoad.NotListed;
        if (states.Any(s => s == "Success"))
            return RepoLoad.Loaded;
        return states.All(s => s == "Fail") ? RepoLoad.Failed : RepoLoad.Loading;
    }

    /// <summary>配布一覧の 1 件（Dalamud の AvailablePlugins の manifest から読んだもの）。</summary>
    public sealed record Candidate(string? InternalName, string? RepoUrl, int ApiLevel);

    /// <summary>
    /// 配布一覧から、入れる版を選ぶ。選べた位置（無ければ -1）と、選べなかった理由を返す。
    ///
    /// 【決めた配布元から来た版だけを選ぶ】配布元を多く登録していると、同じ InternalName の別の人の再配布
    /// （言語版の改造など）も載っている。名前が最初に合ったものを入れると、意図しない版を入れるおそれがある。
    /// 【今の Dalamud で読み込める版だけを選ぶ】Dalamud の配布一覧には、API が 1 つ古い版も「古い」と表示するために残っている
    /// （PluginManager.IsManifestEligible）。入れても読み込めない（LocalPlugin.LoadAsync が API が今より古いと断る）ので選ばない。
    /// </summary>
    public static (int Index, string? Why) Pick(Entry entry, IReadOnlyList<Candidate> candidates, int apiLevel)
    {
        var sameName = Enumerable.Range(0, candidates.Count)
            .Where(i => string.Equals(candidates[i].InternalName, entry.InternalName, StringComparison.Ordinal))
            .ToList();
        var fromRepo = sameName.Where(i => entry.RepoUrls.Any(u => SameUrl(u, candidates[i].RepoUrl))).ToList();
        var usable = fromRepo.Where(i => candidates[i].ApiLevel >= apiLevel).ToList();
        if (usable.Count > 0)
            return (usable[0], null);

        if (fromRepo.Count > 0)
            return (-1, $"配布元の {entry.DisplayName} は API {fromRepo.Max(i => candidates[i].ApiLevel)} 向けで、今の Dalamud（API {apiLevel}）では読み込めません。配布元の更新を待ってください");
        if (sameName.Count > 0)
            return (-1, $"{entry.DisplayName} が決めた配布元（{entry.RepoUrls[0]}）の一覧にありません。ほかの配布元にある同じ名前の版は、別の人の再配布のおそれがあるので入れません");
        return (-1, $"{entry.DisplayName} が配布元（{entry.RepoUrls[0]}）の一覧にありません");
    }
}
