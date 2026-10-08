using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
namespace AutoMobFarm.Farm;

/// <summary>狙った討伐対象の位置。範囲を広げず、次回の高さと重みに使う。</summary>
public sealed record FoundSpot(uint Territory, uint NameId, uint BaseId, float X, float Y, float Z, int Level, string GameVersion, DateTimeOffset At);

/// <summary>見つけた位置の帳面。保存できた位置だけを一覧へ足す。既存の利用者データは変更しない。</summary>
public sealed class FoundSpotStore
{
    public const int CapPerMob = 200;
    public const float SameH = 5f;
    public const float SameV = 5f;
    private readonly string? path;
    private readonly List<FoundSpot> spots = [];
    private readonly List<string> problems = [];
    private bool writeLogged;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public IReadOnlyList<string> LoadProblems => this.problems;
    public string? LastWriteError { get; private set; }
    public int Count => this.spots.Count;
    public FoundSpotStore(string? path)
    {
        this.path = path;
        if (path == null || !File.Exists(path)) return;
        try
        {
            var line = 0;
            foreach (var text in File.ReadLines(path, Encoding.UTF8))
            {
                line++;
                try
                {
                    var s = JsonSerializer.Deserialize<FoundSpot>(text, Json);
                    if (s == null || !Valid(s)) throw new FormatException("位置の値が不正");
                    if (this.CanAdd(s)) this.spots.Add(s);
                }
                catch (Exception ex) { this.problems.Add($"{line}行目：{ex.Message}"); }
            }
        }
        catch (Exception ex) { this.problems.Add($"見つけた位置を読めません：{ex.Message}"); }
    }
    private static bool Valid(FoundSpot s) => s.Territory != 0 && s.NameId != 0 && float.IsFinite(s.X) && float.IsFinite(s.Y) && float.IsFinite(s.Z);
    public IReadOnlyList<FoundSpot> For(uint territory, uint nameId) => this.spots.Where(s => s.Territory == territory && s.NameId == nameId).ToList();
    private bool CanAdd(FoundSpot s)
    {
        var same = this.For(s.Territory, s.NameId);
        return same.Count < CapPerMob && !same.Any(p => Vector2.Distance(new(p.X, p.Z), new(s.X, s.Z)) <= SameH && MathF.Abs(p.Y - s.Y) <= SameV);
    }
    public bool Add(FoundSpot s)
    {
        this.LastWriteError = null;
        if (!Valid(s) || !this.CanAdd(s)) return false;
        try
        {
            if (this.path != null)
            {
                var dir = Path.GetDirectoryName(this.path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(this.path, JsonSerializer.Serialize(s, Json) + "\n", new UTF8Encoding(false));
            }
            this.spots.Add(s);
            return true;
        }
        catch (Exception ex)
        {
            this.LastWriteError = ex.Message;
            if (!this.writeLogged)
            {
                this.writeLogged = true;
                Core.DebugLog.Current?.Line("探索", $"見つけた位置を保存できません：{ex.Message}（収集は続けます）");
            }
            return false;
        }
    }
}
