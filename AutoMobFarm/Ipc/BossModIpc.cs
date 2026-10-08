using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;

namespace AutoMobFarm.Ipc;

/// <summary>
/// BossMod Reborn（BMR）への窓口：AI の入り切り・AI の設定の読み書き・「動かしたいか」「範囲攻撃の中か」を読むことだけ（判断は Farm\BmrAssist）。
///
/// 【名前の前置詞】IPC は "BossMod."（"BossModReborn." ではない。本家 BossMod と同じ名前なので、両方読み込まれていると届く先が決まらない →
///   両方読み込まれていたら使わない）。プラグインの InternalName は "BossModReborn"。
/// 【AI の入り切り】IPC が無い。リフレクションで BossMod.AI.AIManager.Instance（静的）→ Beh（null なら AI は切れている）・MasterSlot、
///   SwitchToFollow(int)・SwitchToIdle() を呼ぶ。チャットの /bmrai on は使わない
///   （追う相手が設定の FollowSlot になり、パーティの誰かを追ってしまうため）。
/// 【設定】BossMod.Configuration(List&lt;string&gt; args, bool save)：["AIConfig", 項目] で読む（値の ToString が1行）、
///   ["AIConfig", 項目, 値] で書く（成功なら空の一覧）。save=false なら設定ファイルへ書かない。
/// 名前と型は BossmodReborn のソース（7.5.6.20：IPCProvider.cs・AIManager.cs・ConfigRoot.cs）で確認し、導入版 7.5.6.33 の DLL にも
/// 同じ名前があることを確かめた。
/// </summary>
public sealed class BossModIpc : IpcGate, Farm.IBossModControl
{
    public override string InternalName => "BossModReborn";

    public override string DisplayName => "BossMod Reborn";

    private const string Prefix = "BossMod.";
    private const string VbmName = "BossMod";

    private bool vbmLoaded;
    private DateTime vbmCheckedAt = DateTime.MinValue;

    // AIManager の型（BMR が読み込み直されたら取り直す）
    private object? cachedPlugin;
    private DateTime cachedAt = DateTime.MinValue;
    private Type? managerType;

    public string? Unusable
    {
        get
        {
            if (!this.IsLoaded)
                return "BossMod Reborn（BMR）が読み込まれていません";
            if (this.VbmLoaded)
                return "本家の BossMod と BMR が両方読み込まれています（IPC の名前が重なるので、BMR を使いません。本家を無効にしてください）";
            return null;
        }
    }

    public bool? AiOn => this.Manager(out var type, out var manager) ? type!.GetField("Beh")?.GetValue(manager) != null : null;

    public int? MasterSlot => this.Manager(out var type, out var manager) && type!.GetField("MasterSlot")?.GetValue(manager) is int slot ? slot : null;

    public bool Follow(int slot)
    {
        if (!this.Manager(out var type, out var manager) || type!.GetMethod("SwitchToFollow", [typeof(int)]) is not { } m)
            return false;
        this.Trace($"AIManager.SwitchToFollow({slot})");
        return this.Call("SwitchToFollow", () => m.Invoke(manager, [slot]));
    }

    public bool AiOff()
    {
        if (!this.Manager(out var type, out var manager) || type!.GetMethod("SwitchToIdle", Type.EmptyTypes) is not { } m)
            return false;
        this.Trace("AIManager.SwitchToIdle()");
        return this.Call("SwitchToIdle", () => m.Invoke(manager, null));
    }

    public string? Read(string key)
    {
        if (this.Unusable != null || !this.TryInvoke("Configuration", () => this.Config(["AIConfig", key], false), out var result))
            return null;

        // 読めたときは値の ToString の1行（True/False・数）。見つからない・あいまいなときは説明の行が返るので、値の形でなければ読めないとする
        return result.Count == 1 && (bool.TryParse(result[0], out _) || float.TryParse(result[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out _))
            ? result[0]
            : null;
    }

    public bool Write(string key, string value, bool save)
    {
        if (this.Unusable != null)
            return false;
        this.Trace($"Configuration(AIConfig {key} {value}, save={save})");
        return this.TryInvoke("Configuration", () => this.Config(["AIConfig", key, value], save), out var result) && result.Count == 0;
    }

    public bool? WantsToMove
        => this.Unusable == null && this.TryInvoke("Movement.IsMoving", () => this.Func<bool>(Prefix + "Movement.IsMoving").InvokeFunc(), out var v) ? v : null;

    public bool? IsSafe(Vector3 position)
        => this.Unusable == null && this.TryInvoke("Hints.IsPositionSafe", () => this.Func<Vector3, bool>(Prefix + "Hints.IsPositionSafe").InvokeFunc(position), out var v) ? v : null;

    public string? ActivePreset
        => this.Unusable == null && this.TryInvoke("Presets.GetActive", () => this.Func<string?>(Prefix + "Presets.GetActive").InvokeFunc() ?? string.Empty, out var v) ? v : null;

    public bool SetActivePreset(string name)
    {
        if (this.Unusable != null)
            return false;
        this.Trace($"Presets.SetActive({name})");
        return this.TryInvoke("Presets.SetActive", () => this.Func<string, bool>(Prefix + "Presets.SetActive").InvokeFunc(name), out var ok) && ok;
    }

    public DateTime? LastModified
        => this.Unusable == null && this.TryInvoke("Configuration.LastModified", () => this.Func<DateTime>(Prefix + "Configuration.LastModified").InvokeFunc(), out var v) ? v : null;

    private List<string> Config(List<string> args, bool save)
        => this.Func<List<string>, bool, List<string>>(Prefix + "Configuration").InvokeFunc(args, save);

    private bool VbmLoaded
    {
        get
        {
            if (DateTime.UtcNow - this.vbmCheckedAt < TimeSpan.FromSeconds(5))
                return this.vbmLoaded;
            this.vbmCheckedAt = DateTime.UtcNow;
            try
            {
                this.vbmLoaded = Svc.PluginInterface.InstalledPlugins.Any(p => p.InternalName == VbmName && p.IsLoaded);
            }
            catch
            {
                this.vbmLoaded = false; // 一覧を読めないときは、BMR だけの判定に任せる
            }

            return this.vbmLoaded;
        }
    }

    /// <summary>AIManager の型と実体（BMR が読み込んだ物。読めなければ false）。</summary>
    private bool Manager(out Type? type, out object? manager)
    {
        type = null;
        manager = null;
        if (this.Unusable != null)
            return false;

        try
        {
            if (this.cachedPlugin == null || DateTime.UtcNow - this.cachedAt > TimeSpan.FromSeconds(5))
            {
                var fresh = RsrStateReader.FindPluginInstance(this.InternalName);
                if (!ReferenceEquals(fresh, this.cachedPlugin))
                    this.managerType = null;
                this.cachedPlugin = fresh;
                this.cachedAt = DateTime.UtcNow;
            }

            if (this.cachedPlugin is not { } plugin)
                return false;

            // BMR が読み込んだアセンブリから引く（自分の文脈で解決すると別物になる）
            this.managerType ??= AssemblyLoadContext.GetLoadContext(plugin.GetType().Assembly)?.Assemblies
                .Select(a => a.GetType("BossMod.AI.AIManager", throwOnError: false))
                .FirstOrDefault(t => t != null);
            type = this.managerType;
            manager = type?.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (type == null || manager == null)
            {
                this.Note("BMR の AI（BossMod.AI.AIManager.Instance）が見つかりません（BMR の版が変わった可能性）");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            this.Note($"BMR の AI を読めません: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool Call(string label, Action call)
    {
        try
        {
            call();
            return true;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
            this.Note($"BMR の AI の {label} が失敗しました: {inner.GetType().Name}: {inner.Message}");
            return false;
        }
    }

    private string? lastNote;

    private void Note(string text)
    {
        if (text == this.lastNote)
            return;
        this.lastNote = text;
        Core.DebugLog.Current?.Line("IPC失敗", text);
    }
}
