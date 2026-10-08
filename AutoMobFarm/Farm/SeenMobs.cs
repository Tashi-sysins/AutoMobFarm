using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AutoMobFarm.Judge;

namespace AutoMobFarm.Farm;

/// <summary>収集中に見かけた通常らしい敵の値。出現点や攻撃許可とは別の、レベル表示の根拠。</summary>
public sealed record SeenMob(uint Territory, uint NameId, uint BaseId, int Level,
    float X, float Y, float Z, string GameVersion, DateTimeOffset At);

/// <summary>
/// 見かけた敵の帳面。2秒ごとに写しを読み、保存できた行だけを表示に使う。
/// 対象を探す移動や収集の状態遷移は行わない。保存の失敗で収集を止めない。
/// </summary>
public sealed class SeenMobStore
{
    public const int CapPerMob = 50;
    public const float SameH = 10f;
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private readonly string? path;

    // エリア×名前ごとの記録（重複と上限の判定はこの組の中だけを見る。全件をなめないので、件数が増えても読み込みが遅くならない）
    private readonly Dictionary<(uint Territory, uint Name), List<SeenMob>> byMob = [];
    private int count;
    private readonly List<string> problems = [];
    private readonly Dictionary<(uint Territory, uint Name), IReadOnlyList<int>> levels = [];
    private DateTime? lastScan;
    private bool writeLogged;

    public SeenMobStore(string? path)
    {
        this.path = path;
        if (path == null || !File.Exists(path))
            return;
        try
        {
            var line = 0;
            foreach (var text in File.ReadLines(path, Encoding.UTF8))
            {
                line++;
                try
                {
                    var record = JsonSerializer.Deserialize<SeenMob>(text, Json);
                    if (record == null || !Valid(record))
                        throw new FormatException("見かけた敵の値が不正です");
                    if (this.CanAdd(record))
                        this.Accept(record);
                }
                catch (Exception ex)
                {
                    this.problems.Add($"見かけた敵の記録 {line}行目：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            this.problems.Add($"見かけた敵の記録を読めません：{ex.Message}");
        }
    }

    public int Count => this.count;

    public IReadOnlyList<string> LoadProblems => this.problems;

    public string? LastWriteError { get; private set; }

    /// <summary>そのエリア・名前の記録の写し（追加した順）。</summary>
    public IReadOnlyList<SeenMob> For(uint territory, uint name)
        => this.byMob.TryGetValue((territory, name), out var list) ? list.ToList() : [];

    /// <summary>画面の作り直しで使う集合。読み出すだけでは全記録を走査しない。</summary>
    public IReadOnlyList<int> Levels(uint territory, uint name)
        => this.levels.GetValueOrDefault((territory, name)) ?? [];

    public void Tick(DateTime now, bool enabled, bool running, bool standby,
        PlayerContext me, IEnumerable<MobSnapshot> mobs, IReadOnlySet<uint> territories)
    {
        if (!enabled || !running || standby || !me.Available || me.ReadError != null
            || me.BoundByDuty || me.Directors.Any(IdRules.LeveDirectors.Contains) || !territories.Contains(me.Territory))
            return;
        if (this.lastScan is { } previous && now >= previous && now - previous < Interval)
            return;
        this.lastScan = now;
        foreach (var mob in mobs)
        {
            if (!mob.IsCombatant || !mob.Enemy || mob.Dead || mob.Hp == 0 || mob.Level == 0
                || mob.FateId != 0 || mob.EventIdRaw != 0 || mob.NamePlateIcon != 0 || mob.ReadError != null)
                continue;
            this.Add(new SeenMob(me.Territory, mob.NameId, mob.BaseId, mob.Level,
                mob.Position.X, mob.Position.Y, mob.Position.Z, me.GameVersion, new DateTimeOffset(now)));
        }
    }

    private static bool Valid(SeenMob record)
        => record.Territory != 0 && record.NameId != 0 && record.Level > 0
           && float.IsFinite(record.X) && float.IsFinite(record.Y) && float.IsFinite(record.Z);

    private bool CanAdd(SeenMob record)
    {
        var same = this.byMob.GetValueOrDefault((record.Territory, record.NameId)) ?? [];
        return same.Count < CapPerMob && !same.Any(r => r.Level == record.Level
            && Vector2.Distance(new(r.X, r.Z), new(record.X, record.Z)) <= SameH);
    }

    private void Accept(SeenMob record)
    {
        var key = (record.Territory, record.NameId);
        if (!this.byMob.TryGetValue(key, out var same))
            this.byMob[key] = same = [];
        same.Add(record);
        this.count++;
        this.levels[key] = same.Select(r => r.Level).Distinct().OrderBy(level => level).ToArray();
    }

    public bool Add(SeenMob record)
    {
        this.LastWriteError = null;
        if (!Valid(record) || !this.CanAdd(record))
            return false;
        try
        {
            if (this.path != null)
            {
                var dir = Path.GetDirectoryName(this.path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(this.path, JsonSerializer.Serialize(record, Json) + "\n", new UTF8Encoding(false));
            }
            this.Accept(record);
            return true;
        }
        catch (Exception ex)
        {
            this.LastWriteError = ex.Message;
            if (!this.writeLogged)
            {
                this.writeLogged = true;
                Core.DebugLog.Current?.Line("記録", $"見かけた敵を保存できません：{ex.Message}（収集は続けます）");
            }
            return false;
        }
    }
}
