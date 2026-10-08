using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoMobFarm.Judge;

namespace AutoMobFarm.Observe;

/// <summary>入手ログの1行（drops\drop_obs.jsonl。原データの MobDrop は変えない）。</summary>
public sealed record DropLine
{
    [JsonPropertyName("ts")] public DateTimeOffset Time { get; init; }
    [JsonPropertyName("game_ver")] public string GameVersion { get; init; } = string.Empty;
    [JsonPropertyName("plugin_ver")] public string PluginVersion { get; init; } = string.Empty;
    [JsonPropertyName("territory")] public uint Territory { get; init; }
    [JsonPropertyName("name_id")] public uint NameId { get; init; }
    [JsonPropertyName("base_id")] public uint BaseId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("object_id")] public string ObjectId { get; init; } = string.Empty;
    [JsonPropertyName("event_id")] public uint EventId { get; init; }
    [JsonPropertyName("fate_id")] public ushort FateId { get; init; }

    /// <summary>増えた品（品 ID → 数）。</summary>
    [JsonPropertyName("gained")] public Dictionary<uint, int> Gained { get; init; } = [];

    /// <summary>attributed_candidate（帰属の候補）・ambiguous（曖昧）・nothing（入手なし。落とさない証拠ではない）。</summary>
    [JsonPropertyName("verdict")] public string Verdict { get; init; } = string.Empty;

    [JsonPropertyName("reasons")] public List<string> Reasons { get; init; } = [];
}

/// <summary>
/// 入手ログ：自分の側が最初に攻撃した敵が倒れたら、その前後の所持品の差を記録する。
///  ・帰属の候補にするのは、窓（倒れてから 6秒）の間に倒れた自分の側の敵が1体だけで、取引・製作・採集・会話などの他の入手源が無いときだけ。
///  ・所持品の増加を無条件に直前の敵のドロップと断定しない（それ以外は「曖昧」）。
///  ・落ちなかった試行は「入手なし」として残すが、「ドロップしない」証拠とはしない。確率は保存しない。
/// </summary>
public sealed class DropLog
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string? path;
    private readonly Func<Dictionary<uint, int>?> readInventory;
    private readonly string pluginVersion;

    // 生きていて、自分の側が最初に攻撃している敵（GameObjectId → 最後の写し）
    private readonly Dictionary<ulong, MobSnapshot> ownTagged = [];
    private readonly List<(MobSnapshot Mob, DateTime At, Dictionary<uint, int> Before, uint Territory, string GameVersion, bool OtherSource)> pending = [];
    private Dictionary<uint, int>? last;
    private DateTime lastRead = DateTime.MinValue;

    // 倒れた時刻（確定済みも含む。同じ時間に倒した別の敵を数えるため。30秒で捨てる）
    private readonly List<DateTime> killTimes = [];

    /// <summary>倒れてから所持品の差を取るまでの時間。</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(6);

    /// <summary>所持品の控えを取り直す間隔（倒れる前の控えにする）。</summary>
    public static readonly TimeSpan ReadInterval = TimeSpan.FromMilliseconds(500);

    public DropLog(string? path, Func<Dictionary<uint, int>?> readInventory, string pluginVersion)
    {
        this.path = path;
        this.readInventory = readInventory;
        this.pluginVersion = pluginVersion;
    }

    /// <summary>この起動で書いた行。</summary>
    public List<DropLine> Written { get; } = [];

    /// <param name="mobs">このフレームの敵。</param>
    /// <param name="me">自分の状態。</param>
    /// <param name="otherSource">いま他の入手源（取引・製作・採集・会話・店など）が動いているか。</param>
    public void Tick(IReadOnlyList<MobSnapshot> mobs, PlayerContext me, bool otherSource, DateTime now, DateTimeOffset nowLocal)
    {
        if (!me.Available)
            return;

        // 倒れたかを見る：自分の側が最初に攻撃していた敵が、倒れている・HP 0・見えなくなった
        var seen = new HashSet<ulong>();
        foreach (var m in mobs)
        {
            seen.Add(m.GameObjectId);
            var dead = m.Dead || m.Hp == 0;
            var ownTag = m.CombatTagType == IdRules.TagCharacter && me.OwnSideIds.Contains(m.CombatTaggerId);
            if (!dead && ownTag)
            {
                this.ownTagged[m.GameObjectId] = m;
            }
            else if (dead && this.ownTagged.Remove(m.GameObjectId, out var before))
            {
                this.pending.Add((m with { Name = before.Name }, now, this.last ?? [], me.Territory, me.GameVersion, otherSource));
                this.killTimes.Add(now);
            }
        }

        this.killTimes.RemoveAll(t => now - t > TimeSpan.FromSeconds(30));

        // 見えなくなった（倒れる前に離れた等）ものは控えから外す。倒れたとは数えない
        foreach (var id in this.ownTagged.Keys.Where(id => !seen.Contains(id)).ToList())
            this.ownTagged.Remove(id);

        // 他の入手源が窓の間に動いたら、窓の中の倒した記録すべてに印を付ける
        if (otherSource)
            for (var i = 0; i < this.pending.Count; i++)
                this.pending[i] = this.pending[i] with { OtherSource = true };

        // 窓が過ぎたものを確定する
        var due = this.pending.Where(p => now - p.At >= Window).ToList();
        if (due.Count > 0)
        {
            var after = this.readInventory();
            foreach (var p in due)
            {
                this.pending.Remove(p);
                // 前後 6秒に倒れた別の敵（確定済みも含む）。いれば「前」の控えに別の敵の入手物が混ざりうるので曖昧にする
                var others = this.killTimes.Count(t => Math.Abs((t - p.At).TotalSeconds) <= Window.TotalSeconds) - 1;
                this.Write(p.Mob, p.Before, after, p.Territory, p.GameVersion, p.OtherSource, others, nowLocal);
            }
        }

        // 倒れる前の控えを取る（窓の確定待ちが無いときだけ。窓の中の増加を「前」に入れない）
        if (this.pending.Count == 0 && now - this.lastRead >= ReadInterval)
        {
            this.lastRead = now;
            this.last = this.readInventory();
        }
    }

    private void Write(MobSnapshot mob, Dictionary<uint, int> before, Dictionary<uint, int>? after, uint territory, string gameVersion, bool otherSource, int otherKills, DateTimeOffset nowLocal)
    {
        var reasons = new List<string>();
        var gained = new Dictionary<uint, int>();
        if (after == null || before.Count == 0)
        {
            reasons.Add("所持品を読めなかった（前か後）");
        }
        else
        {
            foreach (var (item, n) in after)
            {
                var d = n - before.GetValueOrDefault(item);
                if (d > 0)
                    gained[item] = d;
            }
        }

        if (otherKills > 0)
            reasons.Add($"同じ時間に自分の側が倒した敵がほかに {otherKills} 体");
        if (otherSource)
            reasons.Add("同じ時間に他の入手源（取引・製作・採集・会話・店など）が動いていた");

        var verdict = reasons.Count > 0 ? "ambiguous" : gained.Count == 0 ? "nothing" : "attributed_candidate";
        if (verdict == "nothing")
            reasons.Add("入手なし（落とさない証拠ではない）");

        var line = new DropLine
        {
            Time = nowLocal,
            GameVersion = gameVersion,
            PluginVersion = this.pluginVersion,
            Territory = territory,
            NameId = mob.NameId,
            BaseId = mob.BaseId,
            Name = mob.Name,
            ObjectId = $"{mob.GameObjectId:X}",
            EventId = mob.EventIdRaw,
            FateId = mob.FateId,
            Gained = gained,
            Verdict = verdict,
            Reasons = reasons,
        };
        this.Written.Add(line);
        if (this.path == null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
            File.AppendAllText(this.path, JsonSerializer.Serialize(line, Json) + "\n", new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Core.DebugLog.Current?.Line("入手ログ", $"書けませんでした: {ex.Message}");
        }
    }
}
