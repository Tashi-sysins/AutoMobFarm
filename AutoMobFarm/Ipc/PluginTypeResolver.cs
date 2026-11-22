using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;

namespace AutoMobFarm.Ipc;

/// <summary>現在読み込まれているプラグインと同じ文脈から型を探す。DLL名には依存しない。</summary>
internal sealed class PluginTypeResolver
{
    private object? owner;
    private readonly Dictionary<string, Type> cache = new(StringComparer.Ordinal);

    public Type? Resolve(object? plugin, params string[] names)
    {
        // 読み込み直し・無効化の直後に古い型を返さない。
        if (!ReferenceEquals(this.owner, plugin))
        {
            this.cache.Clear();
            this.owner = plugin;
        }

        if (plugin == null || names.Length == 0)
            return null;

        var key = string.Join("|", names);
        if (this.cache.TryGetValue(key, out var cached))
            return cached;

        var host = plugin.GetType().Assembly;
        var context = AssemblyLoadContext.GetLoadContext(host);
        foreach (var name in names)
        {
            // 統合後のRSRは本体に型がある。別文脈の古いDLLや他プラグインは検索しない。
            var found = host.GetType(name, throwOnError: false);
            if (found == null && context != null)
                foreach (var assembly in context.Assemblies)
                {
                    if (assembly == host)
                        continue;
                    found = assembly.GetType(name, throwOnError: false);
                    if (found != null)
                        break;
                }

            if (found != null)
            {
                this.cache[key] = found;
                return found;
            }
        }

        // 依存DLLが後から読み込まれる場合に再探索できるよう、見つからなかった結果は記憶しない。
        return null;
    }
}
