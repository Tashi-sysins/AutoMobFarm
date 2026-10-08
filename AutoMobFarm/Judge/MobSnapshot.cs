using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace AutoMobFarm.Judge;

/// <summary>
/// 敵1体の、1フレームの写し（値だけ。ゲームのオブジェクトの参照は持たない）。
/// Dalamud の ObjectTable は枠ごとの入れ物を使い回すので、参照をフレームをまたいで持つと、消えた敵の枠に入った別の敵を指しうる。
/// 次のフレームでは GameObjectId で引き直し、名前 ID・基本 ID も照らす。
/// 読めなかった値は既定値のままにせず <see cref="ReadError"/> に理由を入れる（判定は「判別できない」になる）。
/// </summary>
public sealed record MobSnapshot
{
    /// <summary>GameObjectId（敵は EntityId から作られる。出現ごとに別の値）。</summary>
    public required ulong GameObjectId { get; init; }

    public uint EntityId { get; init; }

    /// <summary>名前 ID（BNpcName）。</summary>
    public uint NameId { get; init; }

    /// <summary>基本 ID（BNpcBase）。</summary>
    public uint BaseId { get; init; }

    /// <summary>敵（ObjectKind.BattleNpc かつ BattleNpcSubKind.Combatant）か。ペット・チョコボ・プレイヤーは false。</summary>
    public bool IsCombatant { get; init; }

    public string Name { get; init; } = string.Empty;

    public Vector3 Position { get; init; }

    public uint Hp { get; init; }

    public uint MaxHp { get; init; }

    /// <summary>戦闘レベル（ICharacter.Level＝CharacterData.Level。ゲームのシートには無く、実行時だけ読める）。読めなければ 0。</summary>
    public byte Level { get; init; }

    public bool Dead { get; init; }

    /// <summary>ターゲットできるか（ゲームの GetIsTargetable）。</summary>
    public bool Targetable { get; init; }

    /// <summary>敵対しているか（CharacterData の IsHostile）。名札が黄色の「自分からは襲ってこない敵」では立たないことがある（<see cref="CanAttack"/>）。</summary>
    public bool Hostile { get; init; }

    /// <summary>
    /// 攻撃の技をこの敵に使えるか（ゲームの ActionManager.CanUseActionOnTarget に「ブリザド」で聞く。RSR の IsEnemy と同じ問い合わせ）。
    /// 自分からは襲ってこない敵（例：黄金のウルクパチャの「アルパカ」）は IsHostile が立たないので、IsHostile だけで敵を決めると
    /// 目に見える近さにいても狙わない。攻撃してよい敵かは、こちらと合わせて見る。
    /// </summary>
    public bool CanAttack { get; init; }

    /// <summary>敵として扱うか（敵対の印が立っている、またはゲームが攻撃の技を使えると答えた）。</summary>
    public bool Enemy => this.Hostile || this.CanAttack;

    /// <summary>戦闘中か（CharacterData の InCombat。誰と戦っているかは問わない）。</summary>
    public bool InCombat { get; init; }

    /// <summary>GameObject.FateId（FATE の敵なら FATE の番号）。</summary>
    public ushort FateId { get; init; }

    /// <summary>GameObject.EventId の原値。</summary>
    public uint EventIdRaw { get; init; }

    /// <summary>GameObject.EventId.ContentId（イベントの種類）。</summary>
    public EventHandlerContent EventContent { get; init; }

    /// <summary>GameObject.NamePlateIconId（自分のクエストの対象などで名札に付く印）。</summary>
    public uint NamePlateIcon { get; init; }

    /// <summary>CharacterData.CombatTagType（0＝誰も付けていない・1＝キャラクター・2＝パーティ〔PvP?〕・4＝不明：ClientStructs の注記）。</summary>
    public byte CombatTagType { get; init; }

    /// <summary>CharacterData.CombatTaggerId（最初に攻撃した者。倒れるまで残る）。</summary>
    public ulong CombatTaggerId { get; init; }

    /// <summary>この敵が狙っている相手（GameObjectId。無ければ 0）。</summary>
    public ulong TargetObjectId { get; init; }

    /// <summary>読めなかった値がある理由（全部読めたら null）。</summary>
    public string? ReadError { get; init; }
}

/// <summary>自分の状態（判定と記録で使う。1フレームの写し）。</summary>
public sealed record PlayerContext
{
    /// <summary>ログインしていて自分のキャラクターが読めるか。</summary>
    public bool Available { get; init; }

    public uint Territory { get; init; }

    public uint MapId { get; init; }

    /// <summary>インスタンスエリアの番号（UIState.PublicInstance。分かれていなければ 0。読めなければ null）。</summary>
    public uint? Instance { get; init; }

    public Vector3 Position { get; init; }

    /// <summary>自分の GameObjectId。</summary>
    public ulong MyObjectId { get; init; }

    /// <summary>自分の側とみなす GameObjectId（自分・ペット・チョコボ・パーティの仲間）。最初に攻撃した者がこの中なら「他人の獲物」ではない。</summary>
    public IReadOnlySet<ulong> OwnSideIds { get; init; } = new HashSet<ulong>();

    /// <summary>コンテンツに参加中か（ConditionFlag.BoundByDuty）。</summary>
    public bool BoundByDuty { get; init; }

    public bool InCombat { get; init; }

    public bool Dead { get; init; }

    /// <summary>いま動いているディレクター（DirectorModule の一覧）のイベントの種類。</summary>
    public IReadOnlyList<EventHandlerContent> Directors { get; init; } = [];

    /// <summary>ゲームの版（ffxivgame.ver と同じ形。読めなければ空）。</summary>
    public string GameVersion { get; init; } = string.Empty;

    /// <summary>読めなかった値がある理由（全部読めたら null）。</summary>
    public string? ReadError { get; init; }
}
