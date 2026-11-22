using System;
using System.Collections;
using System.Reflection;

namespace AutoMobFarm.Ipc;

/// <summary>
/// RSR（RotationSolverReborn）の今の動作モード・範囲攻撃の設定・真偽の設定を、RSR の内部から読む。
/// RSR の IPC には今のモードを読む口が無い（AutorotationActive は「動作中か」だけ）。モードは DataCenter の6つの旗で決まる。
/// 読むだけで、書き換えはしない（切り替えは IPC で行う）。読めなければ null（呼び出し側は「分からない」として扱う）。
/// </summary>
public static class RsrStateReader
{
    public const string InternalName = "RotationSolver";

    /// <summary>直近の失敗（記録用）。</summary>
    public static string? LastError { get; private set; }

    private static string? lastLogged;

    /// <summary>失敗を残す（同じ文言は続けて記録に書かない：毎フレーム読まれても記録を埋めない）。</summary>
    private static void Note(string message)
    {
        LastError = message;
        if (message == lastLogged)
            return;
        lastLogged = message;
        Core.DebugLog.Current?.Line("IPC", message);
    }

    /// <summary>今のモード（Off=0, Auto=1, TargetOnly=2, Manual=3, AutoDuty=4, Henched=5, PvP=6）。読めなければ null。</summary>
    public static byte? ReadMode()
    {
        try
        {
            var dc = DataCenterType();
            if (dc == null)
                return null;

            bool Flag(string name)
                => dc.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is bool b
                    ? b
                    : throw new MissingMemberException(dc.FullName, name);

            LastError = null;
            return ModeFromFlags(Flag("State"), Flag("IsManual"), Flag("IsTargetOnly"), Flag("IsAutoDuty"), Flag("IsHenched"), Flag("IsPvPStateEnabled"));
        }
        catch (Exception ex)
        {
            LastError = $"RSR の今のモードを読めません: {ex.GetType().Name}: {ex.Message}";
            Note(LastError!);
            return null;
        }
    }

    /// <summary>今の範囲攻撃の設定（Off=0, Cleave=1, Full=2）。読めなければ null。</summary>
    public static byte? ReadAoeType()
    {
        try
        {
            var config = Config();
            var value = config?.GetType().GetProperty("AoEType", BindingFlags.Public | BindingFlags.Instance)?.GetValue(config);
            if (value == null || !value.GetType().IsEnum)
            {
                LastError ??= "RSR の範囲攻撃の設定（Service.Config.AoEType）が見つかりません（RSR の版が変わった可能性）";
                return null;
            }

            return Convert.ToByte(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            LastError = $"RSR の範囲攻撃の設定を読めません: {ex.GetType().Name}: {ex.Message}";
            Note(LastError!);
            return null;
        }
    }

    /// <summary>RSR の真偽の設定（ConditionBoolean の Value）を読む。読めなければ null。</summary>
    public static bool? ReadBool(string name)
    {
        try
        {
            var config = Config();
            var cb = config?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(config);
            var value = cb?.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(cb);
            if (value is bool b)
                return b;

            LastError = $"RSR の設定 {name}（Service.Config.{name}.Value）が見つかりません（RSR の版が変わった可能性）";
            return null;
        }
        catch (Exception ex)
        {
            LastError = $"RSR の設定 {name} を読めません: {ex.GetType().Name}: {ex.Message}";
            Note(LastError!);
            return null;
        }
    }

    /// <summary>DataCenter.TargetFreelyOverride（静的 Boolean）を読むだけ。読めなければ null。</summary>
    public static bool? ReadTargetFreelyOverride()
    {
        try
        {
            return DataCenterType()?
                .GetProperty("TargetFreelyOverride", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as bool?;
        }
        catch
        {
            return null;
        }
    }

    public static string AoeName(byte value) => value switch
    {
        0 => "Off",
        1 => "Cleave",
        2 => "Full",
        _ => $"不明({value})",
    };

    /// <summary>6つの旗からモードを決める（RSR の UpdateState の裏返し）。Henched は IsManual も立つので、Manual より先に見る。</summary>
    public static byte ModeFromFlags(bool state, bool manual, bool targetOnly, bool autoDuty, bool henched, bool pvp)
    {
        if (!state)
            return 0;
        if (pvp)
            return 6;
        if (henched)
            return 5;
        if (autoDuty)
            return 4;
        if (targetOnly)
            return 2;
        if (manual)
            return 3;
        return 1;
    }

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

    private static readonly PluginTypeResolver Types = new();
    internal static readonly string[] DataCenterNames = ["RotationSolver.Engine.State.DataCenter", "RotationSolver.Basic.DataCenter"];
    internal static readonly string[] ServiceNames = ["RotationSolver.Engine.Service", "RotationSolver.Basic.Service"];

    private static Type? DataCenterType()
        => RsrType(DataCenterNames);

    private static Type? RsrType(params string[] names)
    {
        var plugin = FindPluginInstance(InternalName);
        var type = Types.Resolve(plugin, names);
        if (plugin == null)
        {
            LastError = "RSR が読み込まれていません";
            return null;
        }

        if (type == null)
            Note($"RSR の内部（{string.Join(" / ", names)}）が見つかりません（RSR の版が変わった可能性）");
        else
            LastError = null;
        return type;
    }

    private static object? Config()
        => RsrType(ServiceNames)?
            .GetProperty("Config", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

    /// <summary>読み込まれているプラグインの本体（見つからなければ null）。</summary>
    internal static object? FindPluginInstance(string internalName)
    {
        var dalamud = Svc.PluginInterface.GetType().Assembly;
        var service = dalamud.GetType("Dalamud.Service`1", throwOnError: true)!;
        var pmType = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager", throwOnError: true)!;
        var pm = service.MakeGenericType(pmType)
            .GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;
        var installed = (IEnumerable)pmType
            .GetProperty("InstalledPlugins", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(pm)!;

        foreach (var entry in installed)
        {
            var et = entry.GetType();
            if (et.GetProperty("InternalName")?.GetValue(entry) as string != internalName)
                continue;
            if (et.GetProperty("IsLoaded")?.GetValue(entry) is not true)
                continue;

            FieldInfo? field = null;
            for (var t = et; t != null && field == null; t = t.BaseType)
                field = t.GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(entry) is { } plugin)
                return plugin;
        }

        return null;
    }
}
