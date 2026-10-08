using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoMobFarm.Farm;
using AutoMobFarm.Judge;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using CsBattleNpcSubKind = FFXIVClientStructs.FFXIV.Client.Game.Object.BattleNpcSubKind;
using CsCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using CsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AutoMobFarm.Game;

/// <summary>
/// 本番の IFarmWorld（Dalamud とゲームを読む）。フレームの始めに <see cref="Refresh"/> で敵と自分の状態の写しを作り、
/// そのフレームの判定・記録は写しだけを使う（ObjectTable の参照をフレームをまたいで持たない）。
/// 生のオフセットは書かない（ClientStructs の構造体の欄で読む）。読めなかった値は写しの ReadError に理由を入れる。
/// </summary>
public sealed unsafe class GameWorld : IFarmWorld
{
    /// <summary>一般アクションの番号（GeneralAction シートの行。実機で確認済み：マウント・ルーレット 9／降りる 23／ジャンプ 2）。</summary>
    public const uint GeneralMount = 9;

    public const uint GeneralDismount = 23;

    public const uint GeneralJump = 2;

    /// <summary>一般アクション「攻撃」（オートアタックの切り替え。トグル。RSR の AutoAttackUpdater と同じ番号：reference_fate_combat_control）。</summary>
    public const uint GeneralAttack = 1;

    private List<MobSnapshot> mobs = [];
    private Dictionary<ulong, MobSnapshot> byId = [];
    private PlayerContext player = new();
    private List<ulong> haters = [];

    public DateTime Now => DateTime.UtcNow;

    public PlayerContext Player => this.player;

    public IReadOnlyList<MobSnapshot> Mobs => this.mobs;

    public IReadOnlyList<ulong> Haters => this.haters;

    /// <summary>ゲームの版（Lumina のリポジトリ ffxiv の版。保存データの版と同じ読み方）。</summary>
    public static string GameVersion()
    {
        try
        {
            return Svc.Data.GameData.Repositories.TryGetValue("ffxiv", out var repo) ? repo.Version ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>フレームの始めに、敵・自分・敵視リストの写しを作る。</summary>
    public void Refresh()
    {
        this.player = ReadPlayer();
        var list = new List<MobSnapshot>();
        var myId = Svc.Objects.LocalPlayer?.EntityId;
        bool ownedPet = false, ownedBuddy = false;
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc || obj.ObjectKind != ObjectKind.BattleNpc)
                continue;
            list.Add(ReadMob(npc));

            // 自分が持ち主のペット・チョコボ（同じ走査で数える。R4）
            if (myId is { } id && obj.OwnerId == id)
            {
                ownedPet |= obj.SubKind == (byte)CsBattleNpcSubKind.Pet;
                ownedBuddy |= obj.SubKind == (byte)CsBattleNpcSubKind.Buddy;
            }
        }

        this.ownedPet = myId == null ? null : ownedPet;
        this.ownedBuddy = myId == null ? null : ownedBuddy;
        this.mobs = list;
        this.byId = list.GroupBy(m => m.GameObjectId).ToDictionary(g => g.Key, g => g.First());
        this.haters = ReadHaters(list);
    }

    public MobSnapshot? FindMob(ulong id) => this.byId.GetValueOrDefault(id);

    private static PlayerContext ReadPlayer()
    {
        var me = Svc.Objects.LocalPlayer;
        if (!Svc.ClientState.IsLoggedIn || me == null)
            return new PlayerContext { Available = false, GameVersion = GameVersion() };

        var errors = new List<string>();
        uint? instance = null;
        var directors = new List<EventHandlerContent>();
        try
        {
            var ui = UIState.Instance();
            instance = ui != null ? ui->PublicInstance.InstanceId : null;
            if (ui == null)
                errors.Add("UIState が読めない");
        }
        catch (Exception ex)
        {
            errors.Add($"インスタンス番号: {ex.Message}");
        }

        try
        {
            var fw = EventFramework.Instance();
            if (fw == null)
            {
                errors.Add("EventFramework が読めない");
            }
            else
            {
                foreach (var ptr in fw->DirectorModule.DirectorList.AsSpan())
                {
                    var d = ptr.Value;
                    if (d != null && d->EventHandlerInfo != null)
                        directors.Add(d->EventHandlerInfo->EventId.ContentId);
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"ディレクター: {ex.Message}");
        }

        var own = new HashSet<ulong> { me.GameObjectId };
        try
        {
            if (Svc.Buddies.PetBuddy?.GameObject is { } pet)
                own.Add(pet.GameObjectId);
            if (Svc.Buddies.CompanionBuddy?.GameObject is { } chocobo)
                own.Add(chocobo.GameObjectId);
            foreach (var m in Svc.Party)
                if (m.GameObject is { } go)
                    own.Add(go.GameObjectId);
        }
        catch (Exception ex)
        {
            errors.Add($"仲間: {ex.Message}");
        }

        var c = Svc.Condition;
        return new PlayerContext
        {
            Available = true,
            Territory = Svc.ClientState.TerritoryType,
            MapId = Svc.ClientState.MapId,
            Instance = instance,
            Position = me.Position,
            MyObjectId = me.GameObjectId,
            OwnSideIds = own,
            BoundByDuty = c[ConditionFlag.BoundByDuty] || c[ConditionFlag.BoundByDuty56] || c[ConditionFlag.BoundByDuty95],
            InCombat = c[ConditionFlag.InCombat],
            Dead = me.IsDead,
            Directors = directors,
            GameVersion = GameVersion(),
            ReadError = errors.Count > 0 ? string.Join("・", errors) : null,
        };
    }

    /// <summary>「攻撃の技を使えるか」を聞くときの技（142＝ブリザド。RSR の ObjectHelper.IsEnemy と同じ）。射程や詠唱は見ず、相手の種類だけを見る問い合わせ。</summary>
    private const uint EnemyProbeAction = 142;

    private static MobSnapshot ReadMob(IBattleNpc npc)
    {
        var id = npc.GameObjectId;
        try
        {
            var go = (CsGameObject*)npc.Address;
            var ch = (CsCharacter*)npc.Address;
            if (go == null)
                return new MobSnapshot { GameObjectId = id, ReadError = "アドレスが無い" };
            var flags = npc.StatusFlags;
            var combatant = npc.BattleNpcKind == BattleNpcSubKind.Combatant;
            return new MobSnapshot
            {
                GameObjectId = id,
                EntityId = npc.EntityId,
                NameId = npc.NameId,
                BaseId = npc.BaseId,
                IsCombatant = combatant, // Dalamud の列挙は ClientStructs の写し（Combatant＝敵・衛兵）
                Name = npc.Name.TextValue,
                Position = npc.Position,
                Hp = npc.CurrentHp,
                MaxHp = npc.MaxHp,
                Level = npc.Level,
                Dead = npc.IsDead,
                Targetable = npc.IsTargetable,
                Hostile = (flags & StatusFlags.Hostile) != 0,
                // 名札が黄色の敵は IsHostile が立たないことがあるので、攻撃の技を使えるかもゲームに聞く（敵・ターゲットできる物だけ。毎フレーム全員には聞かない）
                CanAttack = combatant && npc.IsTargetable && FFXIVClientStructs.FFXIV.Client.Game.ActionManager.CanUseActionOnTarget(EnemyProbeAction, go),
                InCombat = (flags & StatusFlags.InCombat) != 0,
                FateId = go->FateId,
                EventIdRaw = go->EventId.Id,
                EventContent = go->EventId.ContentId,
                NamePlateIcon = go->NamePlateIconId,
                CombatTagType = ch->CharacterData.CombatTagType,
                CombatTaggerId = ch->CharacterData.CombatTaggerId.Id,
                TargetObjectId = npc.TargetObjectId,
            };
        }
        catch (Exception ex)
        {
            return new MobSnapshot { GameObjectId = id, NameId = SafeNameId(npc), ReadError = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    private static uint SafeNameId(IBattleNpc npc)
    {
        try
        {
            return npc.NameId;
        }
        catch
        {
            return 0;
        }
    }

    private static List<ulong> ReadHaters(List<MobSnapshot> mobs)
    {
        var result = new List<ulong>();
        try
        {
            var ui = UIState.Instance();
            if (ui == null)
                return result;
            ref var hater = ref ui->Hater;
            var count = Math.Clamp(hater.HaterCount, 0, hater.Haters.Length);
            for (var i = 0; i < count; i++)
            {
                var entity = hater.Haters[i].EntityId;
                if (entity == 0)
                    continue;
                // 敵視リストは EntityId で持つ。敵の GameObjectId は EntityId から作られる（ClientStructs の GameObjectId の注記）。表で照らして ID にする
                var mob = mobs.FirstOrDefault(m => m.EntityId == entity);
                result.Add(mob?.GameObjectId ?? entity);
            }
        }
        catch (Exception ex)
        {
            Core.DebugLog.Current?.Line("読み取り", $"敵視リストを読めません: {ex.Message}");
        }

        return result;
    }

    // ---- 所持品 ----

    private static readonly InventoryType[] Bags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    /// <summary>
    /// 鞄の外に入る品の入れ物（シャード・クリスタル・クラスタ＝Crystals、同盟記章などの通貨＝Currency）。
    /// 候補データの 342 品のうち 13 品（品 4〜13・27・10307・26533）はここに入る（鞄だけ数えると目標数で止まらない）。
    /// </summary>
    private static readonly InventoryType[] SpecialContainers = [InventoryType.Crystals, InventoryType.Currency];

    private static int Scan(InventoryContainer* c, uint itemId)
    {
        var n = 0;
        for (var i = 0; i < c->Size; i++)
        {
            var slot = c->GetInventorySlot(i);
            if (slot != null && slot->ItemId != 0 && slot->GetBaseItemId() == itemId)
                n += slot->Quantity;
        }

        return n;
    }

    public int? CountItem(uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return null;
        var total = 0;
        foreach (var type in Bags)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded)
                return null; // 読めない入れ物が1つでもあれば数えない（一部だけ正しい数より、分からないほうがよい）
            total += Scan(c, itemId);
        }

        // 鞄の外の入れ物。クリスタルの入れ物は読み込み済みと報告しないことがあるので、
        // 入れ物を直接読んだ数とゲームの数え方（GetItemCountInContainer）の多いほうを採る（片方だけを信じない）
        foreach (var type in SpecialContainers)
        {
            var c = im->GetInventoryContainer(type);
            var scanned = c != null && c->IsLoaded ? Scan(c, itemId) : 0;
            var game = 0;
            try
            {
                game = im->GetItemCountInContainer(itemId, type);
            }
            catch (Exception ex)
            {
                Core.DebugLog.Current?.Line("読み取り", $"GetItemCountInContainer を呼べません（{type}）: {ex.Message}");
            }

            total += Math.Max(scanned, game);
        }

        return total;
    }

    /// <summary>入手ログ用：鞄 1〜4・クリスタル・通貨の入れ物の、品 ID（NQ・HQ を合わせる）→ 数。鞄が読めなければ null（鞄の外の入れ物は読めた分だけ）。</summary>
    public static Dictionary<uint, int>? ReadInventoryMap()
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return null;
        var map = new Dictionary<uint, int>();
        foreach (var type in Bags.Concat(SpecialContainers))
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded)
            {
                if (Bags.Contains(type))
                    return null;
                continue;
            }

            for (var i = 0; i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    continue;
                var id = slot->GetBaseItemId();
                map[id] = map.GetValueOrDefault(id) + slot->Quantity;
            }
        }

        return map;
    }

    /// <summary>入手ログ用：所持品が敵のドロップ以外で増えうる状態か（取引・製作・採集・釣り・会話・イベント・店など）。</summary>
    public static bool OtherItemSourceActive
    {
        get
        {
            var c = Svc.Condition;
            return c[ConditionFlag.TradeOpen] || c[ConditionFlag.Crafting] || c[ConditionFlag.ExecutingCraftingAction]
                   || c[ConditionFlag.Gathering] || c[ConditionFlag.ExecutingGatheringAction] || c[ConditionFlag.Fishing]
                   || c[ConditionFlag.OccupiedInEvent] || c[ConditionFlag.OccupiedInQuestEvent] || c[ConditionFlag.OccupiedSummoningBell]
                   || c[ConditionFlag.OccupiedInCutSceneEvent] || c[ConditionFlag.Occupied] || c[ConditionFlag.Occupied30]
                   || c[ConditionFlag.Occupied33] || c[ConditionFlag.Occupied38] || c[ConditionFlag.Occupied39];
        }
    }

    public bool? HasRoomFor(uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return null;
        var stack = Svc.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var row) ? (int)row.StackSize : 1;

        // 鞄の外の入れ物（クリスタル・通貨）に既にあれば、そこへ入る。積み重ねの余りだけを見る
        foreach (var type in SpecialContainers)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded)
                continue;
            var have = Scan(c, itemId);
            if (have > 0)
                return have < stack;
        }

        foreach (var type in Bags)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded)
                return null;
            for (var i = 0; i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot == null)
                    continue;
                if (slot->ItemId == 0)
                    return true;
                if (slot->GetBaseItemId() == itemId && slot->Quantity < stack)
                    return true;
            }
        }

        return false;
    }

    // ---- 自分の状態 ----

    public bool InCombat => Svc.Condition[ConditionFlag.InCombat];

    public bool PlayerDead => Svc.Objects.LocalPlayer is { } me && me.IsDead;

    public bool Mounted => Svc.Condition[ConditionFlag.Mounted];

    public bool Flying => Svc.Condition[ConditionFlag.InFlight];

    public bool Casting => Svc.Objects.LocalPlayer is { } me && me.IsCasting;

    public bool Swimming => Svc.Condition[ConditionFlag.Swimming];

    public Vector3 MyPosition => Svc.Objects.LocalPlayer?.Position ?? this.player.Position;

    public bool BetweenAreas => Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51];

    public bool MountingInProgress
    {
        get
        {
            var c = Svc.Condition;
            return c[ConditionFlag.Mounting] || c[ConditionFlag.Mounting71] || c[ConditionFlag.Casting] || c[ConditionFlag.MountOrOrnamentTransition];
        }
    }

    public bool PlayerFree
    {
        get
        {
            if (!Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer == null)
                return false;
            var c = Svc.Condition;
            return !(c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51]
                     || c[ConditionFlag.OccupiedInEvent] || c[ConditionFlag.OccupiedInQuestEvent]
                     || c[ConditionFlag.OccupiedInCutSceneEvent] || c[ConditionFlag.WatchingCutscene]
                     || c[ConditionFlag.Casting] || c[ConditionFlag.Occupied] || c[ConditionFlag.Occupied30]
                     || c[ConditionFlag.Occupied33] || c[ConditionFlag.Occupied38] || c[ConditionFlag.Occupied39]
                     || c[ConditionFlag.Jumping] || c[ConditionFlag.Mounting] || c[ConditionFlag.Mounting71]
                     || c[ConditionFlag.MountOrOrnamentTransition]);
        }
    }

    /// <summary>
    /// チョコボを出しているか：バディ一覧に加えて、物体の表に自分が持ち主の Buddy（ClientStructs の BattleNpcSubKind.Buddy）があるかも見る。
    /// 自分のキャラクターを読めなければ null（出ていないと決めつけない）。
    /// </summary>
    /// <summary>環境画面だけが使う呼び出し状況。戦闘のインターフェースには公開しない。</summary>
    public (bool? Buddy, bool? Pet) Summons => (this.BuddyOut, this.PetOut);

    public bool? BuddyOut => Svc.Buddies.CompanionBuddy != null ? true : this.ownedBuddy;

    /// <summary>
    /// ペットを出しているか：バディ一覧に加えて、物体の表に自分が持ち主の Pet（カーバンクル・フェアリー・オートタレット／オートマトン・クイーン・
    /// リリーベル等。ClientStructs の注記）があるかも見る（<see cref="Refresh"/> の走査で数える）。読めなければ null。
    /// </summary>
    public bool? PetOut => Svc.Buddies.PetBuddy != null ? true : this.ownedPet;

    private bool? ownedPet;
    private bool? ownedBuddy;

    public bool? CombatJob
    {
        get
        {
            var me = Svc.Objects.LocalPlayer;
            if (me == null || !Svc.Data.GetExcelSheet<ClassJob>().TryGetRow(me.ClassJob.RowId, out var row))
                return null;

            // 製作・採集の番号（DohDolJobIndex）を持たず、経験値の欄を持ち、制限ジョブ（青魔道士など）でないもの（番号を決め打ちしない）
            return row.DohDolJobIndex < 0 && row.ExpArrayIndex >= 0 && !row.IsLimitedJob;
        }
    }

    public string JobName
        => Svc.Objects.LocalPlayer is { } me && Svc.Data.GetExcelSheet<ClassJob>().TryGetRow(me.ClassJob.RowId, out var row)
            ? $"{row.Name.ExtractText()} Lv{me.Level}"
            : "（読めない）";

    public bool? MeleeRole
        => Svc.Objects.LocalPlayer is { } me && Svc.Data.GetExcelSheet<ClassJob>().TryGetRow(me.ClassJob.RowId, out var row)
            ? row.Role is 1 or 2 // ClassJob.Role：1＝タンク・2＝近接（AttackReach と同じ見分け）
            : null;

    // ---- 操作 ----

    public ulong HardTargetId => Svc.Targets.Target?.GameObjectId ?? 0;

    public void SetHardTarget(IFoe? foe)
    {
        if (foe == null)
        {
            Svc.Targets.Target = null;
            return;
        }

        // 置く直前に表から引き直し、名前・基本 ID が同じときだけ置く（枠の再利用で別の敵を狙わない）
        if (Svc.Objects.SearchById(foe.Id) is IBattleNpc npc && npc.NameId == foe.NameId && npc.BaseId == foe.BaseId)
            Svc.Targets.Target = npc;
    }

    public bool? InReach(IFoe foe, out uint code)
    {
        code = 0;
        return Svc.Objects.SearchById(foe.Id) is IBattleNpc npc && npc.NameId == foe.NameId ? Data.AttackReach.InReach(npc, out code) : null;
    }

    private static uint GeneralStatus(uint id) => ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, id);

    private static bool UseGeneral(uint id)
    {
        var ok = ActionManager.Instance()->UseAction(ActionType.GeneralAction, id);
        Core.DebugLog.Current?.Line("操作", $"一般アクション {id} を使用 → {(ok ? "受け付け" : "拒否")}");
        return ok;
    }

    public void Dismount() => UseGeneral(GeneralDismount);

    public bool DismountReady => GeneralStatus(GeneralDismount) == 0;

    public bool MountActionReady => GeneralStatus(GeneralMount) == 0;

    public bool CanMountNow => !this.Mounted && !this.InCombat && CanMountInTerritory() && this.MountActionReady;

    public bool CanFlyHere
    {
        get
        {
            var ps = PlayerState.Instance();
            return ps != null && ps->CanFly;
        }
    }

    public void Mount() => UseGeneral(GeneralMount);

    public bool? AutoAttacking
    {
        get
        {
            var ui = UIState.Instance();
            return ui == null ? null : ui->WeaponState.AutoAttackState.IsAutoAttacking;
        }
    }

    public void ToggleAutoAttack() => UseGeneral(GeneralAttack);

    public void Jump() => UseGeneral(GeneralJump);

    private static bool CanMountInTerritory()
        => Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(Svc.ClientState.TerritoryType, out var t) && t.Mount;

    public (float TopY, ulong Material)? SkyAbove(Vector3 p)
    {
        if (FFXIVClientStructs.FFXIV.Common.Component.BGCollision.BGCollisionModule.RaycastMaterialFilter(
                new Vector3(p.X, p.Y + 80f, p.Z), new Vector3(0, -1, 0), out var hit, 120f))
            return (hit.Point.Y, hit.Material);
        return null;
    }

    public float? GroundBelow(float x, float z)
    {
        var origin = new Vector3(x, 1500f, z);
        var direction = new Vector3(0, -1, 0);
        return FFXIVClientStructs.FFXIV.Common.Component.BGCollision.BGCollisionModule.RaycastMaterialFilter(origin, direction, out var hit, 3000f) ? hit.Point.Y : null;
    }

    public (Vector3 Point, ulong Material)? RsrSightBlocked(IFoe foe)
    {
        // RSR の ObjectHelper.CanSeeFrom と同じ：自分の足もと+2m から相手の足もと+2m へ（ClientStructs の既製の呼び方は RSR と同じ絞り・層）
        var from = this.MyPosition + new Vector3(0, 2f, 0);
        var offset = foe.Position + new Vector3(0, 2f, 0) - from;
        var length = offset.Length();
        if (length < 0.01f)
            return null;
        if (FFXIVClientStructs.FFXIV.Common.Component.BGCollision.BGCollisionModule.RaycastMaterialFilter(from, offset / length, out var hit, length))
            return (hit.Point, hit.Material);
        return null;
    }
}
