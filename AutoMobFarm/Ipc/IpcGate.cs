using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace AutoMobFarm.Ipc;

/// <summary>
/// 他プラグインへ IPC を投げるときの共通土台。
///
/// 方針（他プラグイン連携で痛い目を見た箇所なので必ず守る）:
///  ・取得に失敗したら「進めてよい」ではなく「進めない」側へ倒す（fail-closed）。
///  ・例外はまとめて握り潰さず、種類ごとに違う文言でログへ残す。
///  ・導入済み判定はキャッシュする。IsLoaded は IPC 呼び出しごとに走るため。
///  ・読めない状態が続くとログが埋まるので間引く。
/// </summary>
public abstract class IpcGate
{
    /// <summary>相手プラグインの InternalName。</summary>
    public abstract string InternalName { get; }

    /// <summary>画面に出すときの表示名。</summary>
    public virtual string DisplayName => this.InternalName;

    private bool loadedCache;
    private DateTime loadedCacheExpiry = DateTime.MinValue;

    private readonly Dictionary<string, DateTime> lastWarnAt = new(StringComparer.Ordinal);
    private static readonly TimeSpan WarnInterval = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, string> lastErrors = new(StringComparer.Ordinal);

    /// <summary>直近に失敗した IPC の内容。ラベルごとに1件。</summary>
    public IReadOnlyDictionary<string, string> LastErrors => this.lastErrors;

    /// <summary>相手が導入済みかつロード済みか。5秒キャッシュ。</summary>
    public bool IsLoaded
    {
        get
        {
            var now = DateTime.UtcNow;
            if (now < this.loadedCacheExpiry)
                return this.loadedCache;

            try
            {
                this.loadedCache = Svc.PluginInterface.InstalledPlugins
                    .Any(x => x.InternalName == this.InternalName && x.IsLoaded);
            }
            catch (Exception ex)
            {
                this.WarnThrottled($"導入状態を取得できませんでした: {ex.Message}");
                this.loadedCache = false;
            }

            this.loadedCacheExpiry = now.AddSeconds(5);
            return this.loadedCache;
        }
    }

    /// <summary>導入されている版（未導入・版の無い manifest なら null）。</summary>
    public string? LoadedVersion
    {
        get
        {
            try
            {
                foreach (var p in Svc.PluginInterface.InstalledPlugins)
                {
                    try
                    {
                        if (p.InternalName == this.InternalName && p.IsLoaded)
                            return p.Version?.ToString();
                    }
                    catch
                    {
                        // 1つ読めなくても続ける
                    }
                }
            }
            catch
            {
                // 一覧が読めない
            }

            return null;
        }
    }

    protected ICallGateSubscriber<TRet> Func<TRet>(string name)
        => Svc.PluginInterface.GetIpcSubscriber<TRet>(name);

    protected ICallGateSubscriber<T1, TRet> Func<T1, TRet>(string name)
        => Svc.PluginInterface.GetIpcSubscriber<T1, TRet>(name);

    protected ICallGateSubscriber<T1, T2, TRet> Func<T1, T2, TRet>(string name)
        => Svc.PluginInterface.GetIpcSubscriber<T1, T2, TRet>(name);

    protected ICallGateSubscriber<T1, T2, T3, TRet> Func<T1, T2, T3, TRet>(string name)
        => Svc.PluginInterface.GetIpcSubscriber<T1, T2, T3, TRet>(name);

    protected ICallGateSubscriber<T1, T2, T3, T4, TRet> Func<T1, T2, T3, T4, TRet>(string name)
        => Svc.PluginInterface.GetIpcSubscriber<T1, T2, T3, T4, TRet>(name);

    /// <summary>値を返す IPC を呼ぶ。成功したら true。呼び出し側は必ず戻り値の bool を見ること。</summary>
    protected bool TryInvoke<T>(string label, Func<T> call, out T value)
    {
        value = default!;

        if (!this.IsLoaded)
            return false;

        try
        {
            value = call();
            this.lastErrors.Remove(label);
            return true;
        }
        catch (IpcNotReadyError)
        {
            this.Fail(label, "NotReady", "まだ IPC が登録されていません（相手の起動待ち）");
            return false;
        }
        catch (IpcTypeMismatchError ex)
        {
            this.Fail(label, "TypeMismatch", $"IPC の型が合いません。相手のソースを確認してください: {ex.Message}");
            return false;
        }
        catch (IpcLengthMismatchError ex)
        {
            this.Fail(label, "LengthMismatch", $"IPC の引数の数が合いません: {ex.Message}");
            return false;
        }
        catch (IpcValueNullError)
        {
            this.Fail(label, "ValueNull", "IPC が null を返しました");
            return false;
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner is System.Reflection.TargetInvocationException { InnerException: { } i })
                inner = i;
            this.Fail(label, inner.GetType().Name, $"相手の処理で例外が発生しました: {inner.GetType().Name}: {inner.Message}");
            return false;
        }
    }

    private void Fail(string label, string kind, string detail)
    {
        if (!this.lastErrors.TryGetValue(label, out var prev) || prev != detail)
            Core.DebugLog.Current?.Line("IPC失敗", $"{this.DisplayName}.{label}: {detail}");
        this.lastErrors[label] = detail;
        this.WarnThrottled($"{label}: {detail}", $"{label}:{kind}");
    }

    /// <summary>値を返さない IPC を呼ぶ。true は「例外が飛ばなかった」だけを意味する。書いたら読み直して確かめる。</summary>
    protected bool TryAction(string label, Action call)
        => this.TryInvoke<bool>(label, () => { call(); return true; }, out _);

    /// <summary>相手の状態を変える呼び出しを記録する。</summary>
    protected void Trace(string text)
        => Core.DebugLog.Current?.Line("IPC", $"{this.DisplayName}: {text}");

    /// <summary>記録してから続けるための小物（式の中で使う。常に true）。</summary>
    protected bool TraceThen(string text)
    {
        this.Trace(text);
        return true;
    }

    protected void WarnThrottled(string message, string? kind = null)
    {
        var key = kind ?? message;
        var now = DateTime.UtcNow;

        if (this.lastWarnAt.TryGetValue(key, out var last) && now - last < WarnInterval)
            return;

        this.lastWarnAt[key] = now;
        Svc.Log.Warning($"[{this.InternalName}] {message}");
    }
}
