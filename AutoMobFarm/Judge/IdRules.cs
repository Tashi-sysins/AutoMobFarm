using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace AutoMobFarm.Judge;

/// <summary>
/// 個体のイベントの種類を分類する決まり。<see cref="Version"/> は入手ログ等に残す識別子。
///
/// イベントの種類は ClientStructs の列挙（EventHandlerContent）の名前で持つ（数値を直書きしない）。
/// RSR（RotationSolverReborn の ObjectHelper.IsOthersPlayersMob）は Quest・BattleLeveDirector・CompanyLeveDirector・TreasureHuntDirector を
/// 「他人のクエスト・リーヴ・宝の地図の敵」、FateDirector を FATE の敵として扱っている（ソースで確認。こちらの実測ではない）。
/// ここではそれを含め、コンテンツ・イベントのディレクターを広めに「イベント個体」とする。
/// こちらから仕掛けるのは EventId・FATE・名札が0の個体だけ。一覧は理由の文言を分けるために使う。
/// </summary>
public static class IdRules
{
    /// <summary>
    /// イベントの分類と入手ログの識別子。旧データの確認状態の切り替えには使わない。
    /// </summary>
    public const string Version = "id-rules/2";

    /// <summary>FATE の敵を示すイベントの種類。</summary>
    public static readonly IReadOnlySet<EventHandlerContent> FateContents = new HashSet<EventHandlerContent>
    {
        EventHandlerContent.FateDirector,
    };

    /// <summary>クエスト・リーヴ・コンテンツなどのイベント個体を示す種類。</summary>
    public static readonly IReadOnlySet<EventHandlerContent> EventContents = new HashSet<EventHandlerContent>
    {
        EventHandlerContent.Quest,
        EventHandlerContent.BattleLeveDirector,
        EventHandlerContent.GatheringLeveDirector,
        EventHandlerContent.CompanyLeveDirector,
        EventHandlerContent.TreasureHuntDirector,
        EventHandlerContent.QuestBattleDirector,
        EventHandlerContent.InstanceContentDirector,
        EventHandlerContent.PublicContentDirector,
        EventHandlerContent.DpsChallengeDirector,
        EventHandlerContent.MassivePcContentDirector,
        EventHandlerContent.GoldSaucerDirector,
        EventHandlerContent.CompanyCraftDirector,
        EventHandlerContent.SkyIslandDirector,
        EventHandlerContent.DeepDungeon,
    };

    /// <summary>自分がリーヴを実行中であることを示すディレクター。</summary>
    public static readonly IReadOnlySet<EventHandlerContent> LeveDirectors = new HashSet<EventHandlerContent>
    {
        EventHandlerContent.BattleLeveDirector,
        EventHandlerContent.GatheringLeveDirector,
        EventHandlerContent.CompanyLeveDirector,
    };

    /// <summary>CombatTagType の「誰も付けていない」。ClientStructs の CharacterData.cs の注記（0＝Not Tagged）。</summary>
    public const byte TagNone = 0;

    /// <summary>CombatTagType の「キャラクターが付けた」。同じ注記（1＝Character Tag）。</summary>
    public const byte TagCharacter = 1;

    /// <summary>イベントの種類の表示名（列挙の名前。数値も添える）。</summary>
    public static string ContentName(EventHandlerContent c)
        => System.Enum.IsDefined(c) ? $"{c}(0x{(ushort)c:X4})" : $"不明(0x{(ushort)c:X4})";
}
