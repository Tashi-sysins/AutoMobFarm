using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AutoMobFarm.Judge;

namespace AutoMobFarm.Farm;

// 収集の状態機械（FarmRunner）・移動（Mover）・戦闘（Engagement）が使う「ゲーム」「RSR」「vnavmesh」「テレポ」の差し替え口。
// 本番は Game\GameWorld・Ipc\RotationSolverIpc・Ipc\VnavmeshIpc・Game\GameTravel が中身。試験は偽物を渡して同じ部品を動かす。

/// <summary>戦う相手（1フレームの写し。参照をフレームをまたいで持たない）。</summary>
public interface IFoe
{
    ulong Id { get; }

    uint NameId { get; }

    uint BaseId { get; }

    string Name { get; }

    uint Hp { get; }

    Vector3 Position { get; }
}

/// <summary>写しから作る IFoe。</summary>
public sealed record SnapshotFoe(MobSnapshot Mob) : IFoe
{
    public ulong Id => this.Mob.GameObjectId;

    public uint NameId => this.Mob.NameId;

    public uint BaseId => this.Mob.BaseId;

    public string Name => this.Mob.Name;

    public uint Hp => this.Mob.Hp;

    public Vector3 Position => this.Mob.Position;
}

/// <summary>戦闘・移動で読むゲームの状態と、ゲームへの操作（ターゲット・乗る・降りる）。</summary>
public interface ICombatWorld
{
    DateTime Now { get; }

    bool InCombat { get; }

    bool PlayerDead { get; }

    bool Mounted { get; }

    /// <summary>飛んでいるか。</summary>
    bool Flying { get; }

    /// <summary>自分が詠唱中か（詠唱中に歩き出すと詠唱が切れる）。</summary>
    bool Casting { get; }

    Vector3 MyPosition { get; }

    ulong HardTargetId { get; }

    /// <summary>ハードターゲットを置く（null で外す）。</summary>
    void SetHardTarget(IFoe? foe);

    /// <summary>攻撃が届くか（射程・視線をゲームに聞く）。聞けなければ null。</summary>
    bool? InReach(IFoe foe, out uint code);

    /// <summary>「降りる」（一般アクション 23）。</summary>
    void Dismount();

    /// <summary>「降りる」を今ゲームが受け付けられるか（GetActionStatus が 0）。</summary>
    bool DismountReady { get; }

    /// <summary>今マウントに乗れるか（乗っていない・戦闘中でない・乗れるエリア・一般アクション 9 が使える）。</summary>
    bool CanMountNow { get; }

    /// <summary>このエリアで飛べるか（風脈を開放済み：PlayerState.CanFly）。</summary>
    bool CanFlyHere { get; }

    /// <summary>マウントに乗る（一般アクション 9：マウント・ルーレット）。</summary>
    void Mount();

    /// <summary>ジャンプする（一般アクション 2）。</summary>
    void Jump();

    /// <summary>深い水に入って泳いでいるか。</summary>
    bool Swimming { get; }

    /// <summary>その位置の真上から真下へレイキャストして、最初に当たった面の高さと材質（当たらなければ null）。</summary>
    (float TopY, ulong Material)? SkyAbove(Vector3 p);

    /// <summary>真上から地面を読む。当たり判定が無ければ null。</summary>
    float? GroundBelow(float x, float z);

    /// <summary>RSR の視線の判定（足もと+2m のレイキャスト）で相手が遮られているか。遮られていれば当たった点と材質。</summary>
    (Vector3 Point, ulong Material)? RsrSightBlocked(IFoe foe);

    /// <summary>ゲームのオートアタック（通常攻撃の自動継続）が動いているか（UIState.WeaponState.AutoAttackState.IsAutoAttacking）。読めなければ null。</summary>
    bool? AutoAttacking { get; }

    /// <summary>オートアタックの切り替え（一般アクション 1「攻撃」。トグルなので、動いていると読めたときだけ呼ぶ）。</summary>
    void ToggleAutoAttack();
}

/// <summary>収集の状態機械が読むゲームの状態（戦闘の受け口に足したもの）。</summary>
public interface IFarmWorld : ICombatWorld
{
    /// <summary>このフレームの自分の状態。</summary>
    PlayerContext Player { get; }

    /// <summary>このフレームの敵（ObjectTable の BattleNpc。写し）。</summary>
    IReadOnlyList<MobSnapshot> Mobs { get; }

    /// <summary>GameObjectId で引き直す（見つからなければ null。名前・基本 ID の照合は呼び出し側）。</summary>
    MobSnapshot? FindMob(ulong id);

    /// <summary>敵視リストにいる敵の GameObjectId（UIState.Hater）。</summary>
    IReadOnlyList<ulong> Haters { get; }

    /// <summary>本人の通常の所持品（鞄 1〜4）にあるその品の数（NQ＋HQ）。入れ物が読めなければ null。</summary>
    int? CountItem(uint itemId);

    /// <summary>その品がもう1つ入る余地があるか（空き枠か、積み重ねの余り）。読めなければ null。</summary>
    bool? HasRoomFor(uint itemId);

    /// <summary>エリア移動中か。</summary>
    bool BetweenAreas { get; }

    /// <summary>自由に動ける状態か（会話・カットシーン・詠唱・乗り降りの最中でない）。</summary>
    bool PlayerFree { get; }

    /// <summary>乗り降りの動作中か（Mounting・Casting・MountOrOrnamentTransition）。</summary>
    bool MountingInProgress { get; }

    /// <summary>マウント（一般アクション 9）が今使えるか（GetActionStatus が 0）。</summary>
    bool MountActionReady { get; }

    /// <summary>チョコボ（バディ）を呼び出しているか（読めなければ null：出ていないと決めつけない）。</summary>

    /// <summary>ペットを出しているか（読めなければ null）。</summary>

    /// <summary>いまのジョブが戦闘ジョブか（シートで判定）。読めなければ null。</summary>
    bool? CombatJob { get; }

    /// <summary>いまのジョブの名前（画面・記録用）。</summary>
    string JobName { get; }

    /// <summary>いまのジョブが近接の役割（タンク・近接 DPS）か（BMR に寄らせる距離を決める）。読めなければ null。</summary>
    bool? MeleeRole { get; }
}

/// <summary>
/// RSR の低い層の操作（本番は RotationSolverIpc）：命令を「送る」ことと、内部の実際の値を「読む」ことだけ。
/// 送れた（受け付けた）ことは反映の証拠ではない。反映は読み直して確かめる（RsrController）。
/// 型は導入版 RSR 7.5.6.19 の DLL で確認：ChangeOperatingMode(StateCommandType:byte。Off=0・Henched=5)、OtherCommand(OtherCommandType:byte。Settings=0, string)、
/// DataCenter の旗（bool）、Config.AoEType（Off=0）、Config.TargetFreely・IgnoreNonFateInFate（ConditionBoolean.Value:bool）、DataCenter.TargetFreelyOverride（bool）。
/// </summary>
public interface IRotationControl
{
    /// <summary>外部ターゲット指定の解除を頼む。</summary>
    bool DisableTargetFreelyOverride();
    bool IsLoaded { get; }

    /// <summary>動作モードの命令を送る（受け付けたら true）。</summary>
    bool SendMode(byte mode);

    /// <summary>範囲攻撃の設定の命令を送る（受け付けたら true）。</summary>
    bool SendAoe(byte value);

    /// <summary>真偽の設定の命令を送る（受け付けたら true）。</summary>
    bool SendBool(string name, bool value);

    /// <summary>今の動作モード（読めなければ null）。</summary>
    byte? ReadMode();

    /// <summary>今の範囲攻撃の設定（読めなければ null）。</summary>
    byte? ReadAoe();

    /// <summary>今の真偽の設定（読めなければ null）。</summary>
    bool? ReadBool(string name);

    /// <summary>外部ターゲット指定（TargetFreelyOverride）が有効か（読めなければ null）。</summary>
    bool? ReadTargetFreelyOverride();

    /// <summary>直近の読み取りの失敗（画面・記録用）。</summary>
    string? LastReadError { get; }
}

/// <summary>RSR の元の設定の控え（設定ファイル。読み込み直しても、使う前の設定を取り違えない・黙って捨てない）。</summary>
public interface IRsrStore
{
    /// <summary>控えがある（まだ元へ戻したと確かめていない）。</summary>
    bool RsrHenchedPending { get; set; }

    /// <summary>安全停止で Off のまま保持している（利用者が「元に戻す」を押すまで戻さない）。</summary>
    bool RsrHeld { get; set; }

    byte? RsrOriginalMode { get; set; }

    byte? RsrAoeOriginal { get; set; }

    Dictionary<string, bool> RsrBoolOriginals { get; }

    void Save();
}

/// <summary>戦闘の部品（Engagement）が見る「いま攻撃してよいか」（RsrController が中身）。</summary>
public interface ICombatPermit
{
    /// <summary>RSR が Henched で、範囲攻撃 Off・勝手に狙う設定 false・外部ターゲット指定なしを読み直して確かめた（ハードターゲットを置いてよい）。</summary>
    bool AttackReady { get; }

    /// <summary>攻撃を許してはいけない問題（読めない・反映しない・外部の変更）。無ければ null。</summary>
    string? Problem { get; }
}

/// <summary>vnavmesh の操作（本番は VnavmeshIpc）。</summary>
public interface INavControl
{
    bool IsLoaded { get; }

    bool IsMoving();

    /// <summary>経路をたどって動いている最中か（読めなければ null：「動いていない」と区別する）。</summary>
    bool? FollowingPath();

    /// <summary>いまたどっている経路の終点（読めたら true。経路が無ければ end は null）。読めなければ false。</summary>
    bool TryLastWaypoint(out Vector3? end);

    /// <summary>移動を止める命令を送る（受け付けたら true。止まったかは読み直して確かめる）。</summary>
    bool Stop();

    /// <summary>経路だけを求める（取り消せる）。</summary>
    bool TryPathfindCancelable(Vector3 from, Vector3 to, bool fly, CancellationToken cancel, out Task<List<Vector3>>? task);

    /// <summary>求めた経路をたどらせる。</summary>
    bool MoveAlong(List<Vector3> waypoints, bool fly);

    /// <summary>地図の準備ができていて、床を問い合わせられるか。</summary>
    bool IsReady();

    /// <summary>読み込みを頼む。</summary>
    bool Reload();

    /// <summary>エリアに入ったとき地図を自動で読み込む設定か（OFF だと地図が空のままで、Reload も効かない）。読めなければ null。</summary>
    bool? IsAutoLoad();

    /// <summary>構築の進み具合（0以上1未満＝構築中）。読めなければ null。</summary>
    float? BuildProgress();

    /// <summary>残りの経路の点の数（読めなければ null）。</summary>
    int? NumWaypoints();

    Vector3? PointOnFloor(Vector3 p, bool allowUnlandable, float halfExtentXZ);

    Vector3? NearestPointReachable(Vector3 p, float halfExtentXZ, float halfExtentY);
}

/// <summary>
/// BMR（BossMod Reborn）の低い層の操作（本番は Ipc\BossModIpc）。AI の on/off の IPC は無いので、AI はリフレクション
/// （BossMod.AI.AIManager.Instance の Beh・MasterSlot・SwitchToFollow・SwitchToIdle）、設定は BossMod.Configuration の IPC で読み書きする。
/// 名前は導入版 7.5.6.33 の DLL にあると確認した。送れたことは反映の証拠ではない（BmrAssist が読み直す）。
/// </summary>
public interface IBossModControl
{
    /// <summary>使えない理由（BMR が読み込まれていない・本家 BossMod と同時に読み込まれている〔IPC の名前が重なる〕）。使えれば null。</summary>
    string? Unusable { get; }

    /// <summary>AI が動いているか（AIManager.Instance.Beh が null でない）。読めなければ null。</summary>
    bool? AiOn { get; }

    /// <summary>AI が追う相手のパーティの番号（0＝自分）。読めなければ null。</summary>
    int? MasterSlot { get; }

    /// <summary>AI を入れて、その番号の人を追わせる（SwitchToFollow。0＝自分＝誰も追わない）。呼べたら true。</summary>
    bool Follow(int slot);

    /// <summary>AI を切る（SwitchToIdle）。呼べたら true。</summary>
    bool AiOff();

    /// <summary>AI の設定（AIConfig）の今の値（BMR の ToString の文字列）。読めなければ null。</summary>
    string? Read(string key);

    /// <summary>AI の設定を書く（<paramref name="save"/> が false なら設定ファイルへ書かない）。受け付けたら true。</summary>
    bool Write(string key, string value, bool save);

    /// <summary>BMR が足を動かしたがっているか（Movement.IsMoving：範囲攻撃を避ける・敵へ寄る）。読めなければ null。</summary>
    bool? WantsToMove { get; }

    /// <summary>その位置が範囲攻撃の外か（Hints.IsPositionSafe）。読めなければ null。</summary>
    bool? IsSafe(Vector3 position);

    /// <summary>BMR の有効な自動ローテのプリセット（Presets.GetActive。無ければ空）。読めなければ null。</summary>
    string? ActivePreset { get; }

    /// <summary>自動ローテのプリセットを入れる（Presets.SetActive）。受け付けたら true。</summary>
    bool SetActivePreset(string name);

    /// <summary>BMR の設定が最後に保存（変更の通知）された時刻（Configuration.LastModified）。読めなければ null。</summary>
    DateTime? LastModified { get; }
}

/// <summary>エリア移動（テレポ）の操作（本番は GameTravel：Lifestream）。</summary>
public interface ITravel
{
    /// <summary>テレポを頼める状態か（Lifestream が読み込まれているか）。</summary>
    bool Available { get; }

    /// <summary>そのエリアの解放済みエーテライトのうち、near に近いもの。無ければ null。</summary>
    uint? FindAetheryte(uint territory, Vector3? near);

    /// <summary>テレポを頼む。受け付けられなければ false と理由。</summary>
    bool Teleport(uint aetheryteId, out string? why);

    /// <summary>自分がテレポを詠唱中か。</summary>
    bool CastingTeleport { get; }

    /// <summary>テレポが今使えない理由（GetActionStatus。0＝使える）。</summary>
    uint TeleportStatus { get; }

    /// <summary>Lifestream が断る条件（動作の硬直・自分をターゲットできない）に当たっていないか。</summary>
    bool ReadyToTeleport { get; }
}
