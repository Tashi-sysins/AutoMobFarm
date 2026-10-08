using System.Linq;
using System.Numerics;
using AutoMobFarm.Farm;
using AutoMobFarm.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace AutoMobFarm.Game;

/// <summary>本番の ITravel（Lifestream でテレポする。詠唱とエリア移動はこちらで見る）。</summary>
public sealed unsafe class GameTravel : ITravel
{
    /// <summary>テレポの行動の番号（Action シート。GetActionStatus と詠唱の判定に使う）。</summary>
    public const uint TeleportActionId = 5;

    private readonly LifestreamIpc lifestream;

    public GameTravel(LifestreamIpc lifestream)
    {
        this.lifestream = lifestream;
    }

    public bool Available => this.lifestream.IsLoaded;

    // 解放済みエーテライトの一覧（1秒使い回す。Dalamud の AetheryteList は1件読むたびにゲームの一覧を作り直すので、毎フレームたどると重い：
    // Dalamud の AetheryteList.cs）
    private System.Collections.Generic.HashSet<uint>? unlocked;
    private System.DateTime unlockedAt = System.DateTime.MinValue;

    public uint? FindAetheryte(uint territory, Vector3? near)
    {
        if (this.unlocked == null || System.DateTime.UtcNow - this.unlockedAt > System.TimeSpan.FromSeconds(1))
        {
            this.unlocked = Svc.Aetherytes.Select(a => a.AetheryteId).ToHashSet();
            this.unlockedAt = System.DateTime.UtcNow;
        }

        var unlocked = this.unlocked;
        var candidates = Svc.Data.GetExcelSheet<Aetheryte>()
            .Where(a => a.IsAetheryte && a.Territory.RowId == territory && unlocked.Contains(a.RowId))
            .ToList();
        if (candidates.Count == 0)
            return null;
        if (near is not { } n || candidates.Count == 1)
            return candidates[0].RowId;
        return candidates
            .OrderBy(a => Data.AetherytePlaces.Of(a.RowId) is { } p ? Vector2.Distance(p, new Vector2(n.X, n.Z)) : float.MaxValue)
            .First().RowId;
    }

    public bool Teleport(uint aetheryteId, out string? why)
    {
        why = null;
        if (!this.lifestream.TryTeleport(aetheryteId, 0, out var accepted))
        {
            why = "Lifestream を呼べませんでした";
            return false;
        }

        if (!accepted)
            why = "Lifestream がテレポを受け付けませんでした";
        return accepted;
    }

    public bool CastingTeleport
        => Svc.Objects.LocalPlayer is { } me && me.IsCasting && me.CastActionId == TeleportActionId && me.CastActionType == (byte)ActionType.Action;

    public uint TeleportStatus => ActionManager.Instance()->GetActionStatus(ActionType.Action, TeleportActionId);

    /// <summary>
    /// Lifestream がテレポを断る条件に当たっていないか（Lifestream の TeleportService.CanTeleport：動作の硬直中・自分をターゲットできない）。
    /// 頼む前の点検（確かめないと、断られて回数を使い切る）。
    /// </summary>
    public bool ReadyToTeleport => ActionManager.Instance()->AnimationLock <= 0 && Svc.Objects.LocalPlayer is { IsTargetable: true };
}
