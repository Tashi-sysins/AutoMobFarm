using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AutoMobFarm.Core;

/// <summary>
/// 動作の記録（日本語の1行ずつ）。設定フォルダーの logs\全体_日付.log に追記し、画面用に新しい 300 行を持つ。
/// 開発機固有の場所を既定にしない（そうすると配布版で1行も残らない）。
/// 試験では <see cref="Current"/> に書き込み先の無いものを入れる（ファイルに書かない）。
/// </summary>
public sealed class DebugLog
{
    /// <summary>いま使っている記録（プラグインが起動時に入れる。無ければ記録しない）。</summary>
    public static DebugLog? Current { get; set; }

    private readonly string? dir;
    private readonly Func<DateTime> clock;
    private readonly LinkedList<string> recent = new();
    private readonly object gate = new();

    /// <summary>画面に出す新しい行の上限。</summary>
    public const int RecentLimit = 300;

    /// <param name="dir">書き込み先のフォルダー（null ならファイルに書かない）。</param>
    /// <param name="clock">時刻（試験で差し替える）。</param>
    public DebugLog(string? dir, Func<DateTime>? clock = null)
    {
        this.dir = dir;
        this.clock = clock ?? (() => DateTime.Now);
        if (dir != null)
            Directory.CreateDirectory(dir);
    }

    /// <summary>書き込み先のファイル（今日の分）。書かない設定なら null。</summary>
    public string? FilePath => this.dir == null ? null : Path.Combine(this.dir, $"全体_{this.clock():yyyyMMdd}.log");

    /// <summary>新しい行（古い順）。</summary>
    public IReadOnlyList<string> Recent
    {
        get
        {
            lock (this.gate)
                return [.. this.recent];
        }
    }

    /// <summary>1行書く。</summary>
    public void Line(string category, string text)
    {
        var line = $"{this.clock():HH:mm:ss.fff} [{category}] {text}";
        lock (this.gate)
        {
            this.recent.AddLast(line);
            while (this.recent.Count > RecentLimit)
                this.recent.RemoveFirst();
        }

        if (this.FilePath is not { } path)
            return;
        try
        {
            File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            // 記録できなくても動作は止めない（Dalamud の記録へ1度出す）
            try
            {
                Svc.Log.Warning($"[AutoMobFarm] 記録を書けませんでした: {ex.Message}");
            }
            catch
            {
                // 試験など Svc が無いとき
            }
        }
    }
}
