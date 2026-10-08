namespace AutoMobFarm.Ipc;

/// <summary>
/// Lifestream への窓口（テレポートだけ）。
/// IPC 名は Lifestream の実ソース（Lifestream/IPC/IPCProvider.cs。EzIPC の前置詞 "Lifestream"）で確認済み：bool Teleport(uint destination, byte subIndex)。
/// Teleport は Lifestream 内部の待機なしの経路を直接呼ぶため、呼んだあと IsBusy は true にならない。
/// 詠唱の完了・エリア遷移はこちらで待つ。
/// </summary>
public sealed class LifestreamIpc : IpcGate
{
    public override string InternalName => "Lifestream";

    /// <summary>指定のエーテライトへテレポートする。false なら呼べなかった。accepted は Lifestream が受け付けたか。</summary>
    public bool TryTeleport(uint aetheryteId, byte subIndex, out bool accepted)
    {
        this.Trace($"Teleport(エーテライト {aetheryteId}, {subIndex})");
        return this.TryInvoke("Teleport", () => this.Func<uint, byte, bool>("Lifestream.Teleport").InvokeFunc(aetheryteId, subIndex), out accepted);
    }
}
