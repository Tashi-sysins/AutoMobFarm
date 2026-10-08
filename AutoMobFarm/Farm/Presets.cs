using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoMobFarm.Farm;

/// <summary>
/// 自動討伐のプリセット。設定ファイルに保存する。名前を変えると利用者の値が消えるので、変えるときは旧名を残して移す。
/// </summary>
public sealed class Preset
{
    /// <summary>不変の ID（名前を変えても同じ）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<Goal> Goals { get; set; } = [];

    /// <summary>保存の版（中身を変えるたびに増やす。実行計画は開始時の版を持つ）。</summary>
    public int Revision { get; set; }
}

/// <summary>1品の目標（所持数がこの数に達するまで集める。「開始後に N 個」ではない）。</summary>
public sealed class Goal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public uint ItemId { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>目標の所持数（本人の所持品の NQ＋HQ）。追加の操作で利用者の値を戻さない。</summary>
    public int TargetCount { get; set; } = 1;

    /// <summary>この品を落とすモブの候補（画面には代表の1件だけを出す。集めるときは全部を使う）。</summary>
    public List<SourceMobRef> Sources { get; set; } = [];
}

/// <summary>品を落とすモブの候補（名前 × エリア × 候補地点）。攻撃してよいかは、ここではなくカタログの狩り場と現地の個体の判定で決める。</summary>
public sealed class SourceMobRef
{
    public uint NameId { get; set; }

    public uint Territory { get; set; }

    /// <summary>カタログの候補地点の安定キー（無ければ null：エリア全体）。</summary>
    public string? PointKey { get; set; }

    public bool Enabled { get; set; } = true;

    public bool SameAs(SourceMobRef o) => o.NameId == this.NameId && o.Territory == this.Territory && o.PointKey == this.PointKey;

    public SourceMobRef Copy() => new() { NameId = this.NameId, Territory = this.Territory, PointKey = this.PointKey, Enabled = this.Enabled };
}

/// <summary>追加の結果（どのプリセットに入ったか・新しく作ったか）。</summary>
public sealed record PresetAddResult(Preset Preset, bool CreatedPreset, Goal Goal, bool CreatedGoal);

/// <summary>
/// プリセットの一覧の操作と、単品の追加先の決まり（1 品ずつの追加は、全部 1 つのプリセットへ足していく）。
///  ・モブ・アイテムの表から1品ずつ足した品は、決まった1つのプリセット（最初は「個別追加」の名前で作る）へ足していく。
///    どれがそのプリセットかは ID で覚え（設定に保存）、名前を変えても・再起動しても同じプリセットへ足す。消されたら次の追加で作り直す。
///    （以前の「選んでから 60 秒以内なら選択中へ、過ぎたら品名で新しく作る」決まりは、品名のプリセットが次々に増えるのでやめた）
///  ・同じ品を再追加したら、落とすモブの候補だけを重複なく足し、目標数は変えない。
///  ・帯の一括追加は帯の名前で新しく作る（単品の追加先にはしない）。
/// </summary>
public sealed class PresetBook
{
    /// <summary>単品の追加先のプリセットを新しく作るときの名前。</summary>
    public const string SingleName = "個別追加";

    private readonly List<Preset> presets;

    /// <param name="presets">保存する一覧（設定の中のリストをそのまま渡す）。</param>
    /// <param name="restoredSelectedId">前回の選択（再起動のとき）。</param>
    /// <param name="singleId">単品の追加先のプリセットの ID（設定に保存したもの。一覧に無ければ無いものとする）。</param>
    public PresetBook(List<Preset> presets, string? restoredSelectedId, string? singleId = null)
    {
        this.presets = presets;
        this.SelectedId = presets.Any(p => p.Id == restoredSelectedId) ? restoredSelectedId : null;
        this.SingleId = presets.Any(p => p.Id == singleId) ? singleId : null;
    }

    public IReadOnlyList<Preset> Presets => this.presets;

    public string? SelectedId { get; private set; }

    public Preset? Selected => this.presets.FirstOrDefault(p => p.Id == this.SelectedId);

    /// <summary>単品の追加先のプリセットの ID（まだ無ければ null）。設定に保存する。</summary>
    public string? SingleId { get; private set; }

    /// <summary>選ぶ（画面の左の一覧で押したとき）。</summary>
    public void Select(string id)
    {
        if (this.presets.All(p => p.Id != id))
            return;
        this.SelectedId = id;
    }

    /// <summary>明示的に新しく作って選ぶ。</summary>
    public Preset Create(string name)
    {
        var p = new Preset { Name = this.UniqueName(string.IsNullOrWhiteSpace(name) ? "新しいプリセット" : name.Trim()) };
        this.presets.Add(p);
        this.Select(p.Id);
        return p;
    }

    /// <summary>複製して選ぶ（目標・候補は写し、ID は新しくする）。</summary>
    public Preset? Duplicate(string id)
    {
        var src = this.presets.FirstOrDefault(p => p.Id == id);
        if (src == null)
            return null;
        var copy = new Preset
        {
            Name = this.UniqueName(src.Name),
            Description = src.Description,
            Goals = src.Goals.Select(g => new Goal { ItemId = g.ItemId, Enabled = g.Enabled, TargetCount = g.TargetCount, Sources = g.Sources.Select(s => s.Copy()).ToList() }).ToList(),
        };
        this.presets.Add(copy);
        this.Select(copy.Id);
        return copy;
    }

    public void Delete(string id)
    {
        this.presets.RemoveAll(p => p.Id == id);
        if (this.SelectedId == id)
            this.SelectedId = null;
        if (this.SingleId == id)
            this.SingleId = null; // 次の単品の追加で作り直す
    }

    /// <summary>名前を変える。空なら変えない。前後の空白は落とす。</summary>
    public void Rename(string id, string name)
    {
        if (this.presets.FirstOrDefault(p => p.Id == id) is { } p && !string.IsNullOrWhiteSpace(name))
        {
            p.Name = name.Trim();
            p.Revision++;
        }
    }

    /// <summary>
    /// モブ・アイテムの表から1品を足す。追加先は単品の追加先のプリセット（無ければ「個別追加」の名前で作る）。
    /// 選択中のプリセットや時間には左右されない。
    /// </summary>
    public PresetAddResult AddItem(uint itemId, IEnumerable<SourceMobRef> sources, int defaultTarget)
    {
        var target = this.presets.FirstOrDefault(p => p.Id == this.SingleId);
        var created = false;
        if (target == null)
        {
            target = new Preset { Name = this.UniqueName(SingleName) };
            this.presets.Add(target);
            this.SingleId = target.Id;
            created = true;
        }

        var (goal, createdGoal) = Merge(target, itemId, sources, defaultTarget);
        target.Revision++;
        return new PresetAddResult(target, created, goal, createdGoal);
    }

    /// <summary>帯の品をまとめて足す（帯の名前で新しく作り、それを選ぶ）。</summary>
    public Preset AddBand(string bandName, IEnumerable<(uint ItemId, IEnumerable<SourceMobRef> Sources)> items, int defaultTarget)
    {
        var p = new Preset { Name = this.UniqueName(bandName) };
        this.presets.Add(p);
        foreach (var (item, sources) in items)
            Merge(p, item, sources, defaultTarget);
        p.Revision++;
        this.Select(p.Id);
        return p;
    }

    /// <summary>
    /// 品と目標数の一覧から新しいプリセットを作り、それを選ぶ（Artisan のリストの取り込みなど）。
    /// 同じ品が重なれば目標数を足し、候補は重複なくまとめる。目標数は 1〜9999 に収める。
    /// </summary>
    public Preset AddImported(string name, IEnumerable<(uint ItemId, int Target, IEnumerable<SourceMobRef> Sources)> items)
    {
        var p = new Preset { Name = this.UniqueName(name) };
        this.presets.Add(p);
        foreach (var (item, target, sources) in items)
        {
            var before = p.Goals.FirstOrDefault(g => g.ItemId == item)?.TargetCount ?? 0;
            var (goal, _) = Merge(p, item, sources, target);
            goal.TargetCount = (int)Math.Clamp((long)before + target, 1, 9999);
        }

        p.Revision++;
        this.Select(p.Id);
        return p;
    }

    /// <summary>
    /// 品を入れ替える（自動討伐タブの品の欄で選び直したとき。GBR のプリセットの品の選び直しと同じ）。
    /// 同じ品・ほかの行にある品なら入れ替えない（false）。目標数・有効はそのまま、候補は新しい品の候補に置き換える。
    /// </summary>
    public static bool ChangeItem(Preset p, Goal goal, uint itemId, IEnumerable<SourceMobRef> sources)
    {
        if (itemId == 0 || goal.ItemId == itemId || p.Goals.Any(g => !ReferenceEquals(g, goal) && g.ItemId == itemId))
            return false;
        goal.ItemId = itemId;
        goal.Sources = sources.Select(s => s.Copy()).ToList();
        p.Revision++;
        return true;
    }

    /// <summary>品を1つ手で足す（自動討伐タブの「＋」の行）。もうある品なら足さない（null）。</summary>
    public static Goal? AddGoal(Preset p, uint itemId, int target, IEnumerable<SourceMobRef> sources)
    {
        if (itemId == 0 || p.Goals.Any(g => g.ItemId == itemId))
            return null;
        var (goal, _) = Merge(p, itemId, sources, target);
        goal.TargetCount = Math.Clamp(target, 1, 9999);
        p.Revision++;
        return goal;
    }

    /// <summary>プリセットの全部の品の目標数を同じ数にする（一括変更）。変えた品の数を返す。</summary>
    public static int SetAllTargets(Preset p, int target)
    {
        var value = Math.Clamp(target, 1, 9999);
        var changed = 0;
        foreach (var g in p.Goals.Where(g => g.TargetCount != value))
        {
            g.TargetCount = value;
            changed++;
        }

        if (changed > 0)
            p.Revision++;
        return changed;
    }

    /// <summary>プリセットの全部の品の有効をまとめて切り替える（一括の行のチェック）。変えた品の数を返す。</summary>
    public static int SetAllEnabled(Preset p, bool enabled)
    {
        var changed = 0;
        foreach (var g in p.Goals.Where(g => g.Enabled != enabled))
        {
            g.Enabled = enabled;
            changed++;
        }

        if (changed > 0)
            p.Revision++;
        return changed;
    }

    /// <summary>同じ名前があれば「名前 (2)」「名前 (3)」…にする。</summary>
    public string UniqueName(string baseName)
    {
        if (this.presets.All(p => p.Name != baseName))
            return baseName;
        for (var i = 2; ; i++)
        {
            var name = $"{baseName} ({i})";
            if (this.presets.All(p => p.Name != name))
                return name;
        }
    }

    /// <summary>同じ品があれば候補だけを重複なく足す（目標数は変えない）。無ければ既定の目標数で足す。</summary>
    public static (Goal Goal, bool Created) Merge(Preset p, uint itemId, IEnumerable<SourceMobRef> sources, int defaultTarget)
    {
        var goal = p.Goals.FirstOrDefault(g => g.ItemId == itemId);
        var created = goal == null;
        if (goal == null)
        {
            goal = new Goal { ItemId = itemId, TargetCount = Math.Max(1, defaultTarget) };
            p.Goals.Add(goal);
        }

        foreach (var s in sources)
        {
            if (!goal.Sources.Any(x => x.SameAs(s)))
                goal.Sources.Add(s.Copy());
        }

        return (goal, created);
    }

    /// <summary>
    /// 読み込んだ一覧の整え（壊れた値を黙って 0 や既定値に変えず、使えないものだけを外して理由を返す）。
    /// ID の重複は新しい ID に振り直す。目標数が 1 未満なら 1 にする（0 個の目標は意味が無い）。
    /// </summary>
    public static List<string> Sanitize(List<Preset> presets)
    {
        var notes = new List<string>();
        var ids = new HashSet<string>();
        foreach (var p in presets.ToList())
        {
            if (p == null)
            {
                presets.Remove(p!);
                notes.Add("空のプリセットを外しました");
                continue;
            }

            p.Goals ??= [];
            p.Name ??= string.Empty;
            p.Description ??= string.Empty;
            if (string.IsNullOrEmpty(p.Id) || !ids.Add(p.Id))
            {
                p.Id = Guid.NewGuid().ToString("N");
                ids.Add(p.Id);
                notes.Add($"プリセット「{p.Name}」の ID が無い・重複していたので振り直しました");
            }

            foreach (var g in p.Goals.ToList())
            {
                if (g == null || g.ItemId == 0)
                {
                    p.Goals.Remove(g!);
                    notes.Add($"プリセット「{p.Name}」の品の無い目標を外しました");
                    continue;
                }

                g.Sources ??= [];
                if (string.IsNullOrEmpty(g.Id))
                    g.Id = Guid.NewGuid().ToString("N");
                if (g.TargetCount < 1)
                {
                    notes.Add($"プリセット「{p.Name}」の品 {g.ItemId} の目標数 {g.TargetCount} を 1 にしました");
                    g.TargetCount = 1;
                }

                var unique = new List<SourceMobRef>();
                foreach (var s in g.Sources.Where(s => s != null && s.NameId != 0))
                    if (!unique.Any(u => u.SameAs(s)))
                        unique.Add(s);
                g.Sources = unique;
            }
        }

        return notes;
    }
}
