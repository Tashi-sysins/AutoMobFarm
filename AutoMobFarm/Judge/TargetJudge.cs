using System.Collections.Generic;
using System.Linq;

namespace AutoMobFarm.Judge;

/// <summary>判定の結果（強い順。最初に当たったものが結果になり、当たった理由は全部 <see cref="Judgement.Reasons"/> に残す）。</summary>
public enum Verdict
{
    /// <summary>攻撃してよい（カタログの狩り場の通常個体）。</summary>
    Eligible,

    /// <summary>判別できない（読み取り失敗・知らないイベントの種類・最初に攻撃した者の種類が不明・識別できない値）。攻撃しない。</summary>
    Unknown,

    /// <summary>敵ではない（ペット・チョコボ・プレイヤー等）。</summary>
    NotEnemy,

    /// <summary>倒れている（HP 0）。</summary>
    Dead,

    /// <summary>FATE の敵。</summary>
    Fate,

    /// <summary>クエスト・リーヴ・コンテンツなどのイベント個体（名札の印を含む）。</summary>
    Event,

    /// <summary>他人が先に攻撃している（他人の獲物）。</summary>
    ClaimedByOther,

    /// <summary>敵対していない。</summary>
    NotHostile,

    /// <summary>ターゲットできない。</summary>
    NotTargetable,

    /// <summary>自分がコンテンツ参加中・リーヴ実行中（この間は自動攻撃しない）。</summary>
    PlayerBusy,

    /// <summary>カタログの狩り場の水平範囲の外。</summary>
    OutOfGround,

    /// <summary>今回の計画のモブではない（名前・エリアが計画の狩り場に無い）か、基本 ID がカタログの基本 ID の集合に無い。</summary>
    NotInPlan,
}

/// <summary>判定の結果と理由。<paramref name="AreaKey"/>：攻撃してよいとき、入っていた狩り場（それ以外は null）。</summary>
public sealed record Judgement(Verdict Verdict, IReadOnlyList<string> Reasons, string? AreaKey)
{
    public bool Attackable => this.Verdict == Verdict.Eligible;
}

/// <summary>
/// こちらから仕掛けてよい1体を決める（ゲームに触らない。試験で同じものを動かす）。攻撃してきた敵への反撃はこの判定を使わない（FarmRunner：D1）。
///
/// 決め方：
///  ・判定不能・読み取り失敗は「判別できない」として仕掛けない。
///  ・個体の見分け：FATE・イベント（EventId が 0 でない）・名札の印・他人の獲物・倒れている・ターゲットできない・敵対していない、のどれでもないこと。
///    確認記録が無くなったので、EventId・名札は通常個体の値（0）だけを許す。
///  ・自分がコンテンツ参加中・リーヴ実行中なら仕掛けない（リーヴの敵は通常の敵と同じ名前・基本 ID を使うことがあるため）。
///  ・狩り場：今回の計画（<see cref="HuntArea"/> の集合）に同じエリア・名前があり、基本 ID がカタログの集合に入り（空なら問わない）、
///    カタログの点（Anchor）から水平 RadiusH 以内にいること。高さは見ない（カタログに高さが無い）。
/// 巡回先を選ぶこと（PatrolPlan）と、この個体に仕掛けてよいかは別の判定にしてある。
/// </summary>
public static class TargetJudge
{
    /// <summary>判定する。当たった理由は全部 <see cref="Judgement.Reasons"/> に残し、最初に当たったものを結果にする。</summary>
    public static Judgement Judge(MobSnapshot m, PlayerContext me, IReadOnlyList<HuntArea> areas)
    {
        var reasons = new List<string>();
        Verdict? first = null;

        void Hit(Verdict v, string why)
        {
            first ??= v;
            reasons.Add(why);
        }

        if (m.ReadError != null)
            Hit(Verdict.Unknown, $"敵の値を読めない（{m.ReadError}）");
        if (me.ReadError != null || !me.Available)
            Hit(Verdict.Unknown, $"自分の状態を読めない（{me.ReadError ?? "ログインしていない"}）");
        if (!m.IsCombatant)
            Hit(Verdict.NotEnemy, "敵ではない（BattleNpc の Combatant でない）");
        if (m.Dead || m.Hp == 0)
            Hit(Verdict.Dead, "倒れている（HP 0）");

        // FATE：敵の FateId、または FATE のイベント
        if (m.FateId != 0)
            Hit(Verdict.Fate, $"FATE の敵（FateId {m.FateId}）");
        if (IdRules.FateContents.Contains(m.EventContent))
            Hit(Verdict.Fate, $"FATE のイベント（{IdRules.ContentName(m.EventContent)}）");

        // イベントと名札は通常個体の値（0）のみを許す。
        if (m.EventIdRaw != 0)
        {
            if (IdRules.EventContents.Contains(m.EventContent))
                Hit(Verdict.Event, $"イベント個体（{IdRules.ContentName(m.EventContent)}・EventId 0x{m.EventIdRaw:X8}）");
            else if (!IdRules.FateContents.Contains(m.EventContent))
                Hit(Verdict.Unknown, $"知らないイベントの種類（{IdRules.ContentName(m.EventContent)}・EventId 0x{m.EventIdRaw:X8}）");
        }

        if (m.NamePlateIcon != 0)
            Hit(Verdict.Event, $"名札に印がある（{m.NamePlateIcon}。自分のクエスト等の対象）");

        // 他人の獲物：最初に攻撃した者が自分の側でない。種類が分からなければ判別できない
        if (m.CombatTagType == IdRules.TagCharacter)
        {
            if (!me.OwnSideIds.Contains(m.CombatTaggerId))
                Hit(Verdict.ClaimedByOther, "他人が先に攻撃している（最初に攻撃した者が自分・仲間でない）");
        }
        else if (m.CombatTagType != IdRules.TagNone)
        {
            Hit(Verdict.Unknown, $"最初に攻撃した者の種類が分からない（CombatTagType {m.CombatTagType}）");
        }

        // 念のため：戦闘中で、自分の側を狙っていない（他人と戦っている）なら攻撃しない
        if (m.InCombat && m.CombatTagType != IdRules.TagCharacter && m.TargetObjectId != 0 && !me.OwnSideIds.Contains(m.TargetObjectId))
            Hit(Verdict.ClaimedByOther, "戦闘中で、自分・仲間以外を狙っている");

        // 敵対の印（IsHostile）か、ゲームが攻撃の技を使えると答えた敵（名札が黄色の、自分からは襲ってこない敵を含む）
        if (!m.Enemy)
            Hit(Verdict.NotHostile, "敵対していない（攻撃の技を使えない）");
        if (!m.Targetable)
            Hit(Verdict.NotTargetable, "ターゲットできない");

        if (me.BoundByDuty)
            Hit(Verdict.PlayerBusy, "自分がコンテンツに参加中（BoundByDuty）");
        var leve = me.Directors.Where(IdRules.LeveDirectors.Contains).ToList();
        if (leve.Count > 0)
            Hit(Verdict.PlayerBusy, $"自分がリーヴを実行中（{string.Join("・", leve.Select(IdRules.ContentName))}）");

        // 狩り場の名前・エリア・基本 ID・水平範囲
        string? areaKey = null;
        var mine = areas.Where(a => a.Territory == me.Territory && a.NameId == m.NameId).ToList();
        if (mine.Count == 0)
        {
            Hit(Verdict.NotInPlan, "今回の計画のモブではない（名前・エリアが計画に無い）");
        }
        else
        {
            var baseOk = mine.Where(a => a.AcceptsBase(m.BaseId)).ToList();
            if (baseOk.Count == 0)
                Hit(Verdict.NotInPlan, $"基本 ID {m.BaseId} が、カタログのこの名前・エリアの基本 ID（{string.Join("・", mine.SelectMany(a => a.BaseIds).Distinct().OrderBy(b => b))}）に無い");
            else if (baseOk.FirstOrDefault(a => a.Contains(m.Position)) is { } inside)
                areaKey = inside.Key;
            else
                Hit(Verdict.OutOfGround, $"狩り場（カタログの地点から水平 {baseOk[0].RadiusH:0}m）の外（({m.Position.X:0},{m.Position.Y:0},{m.Position.Z:0})）");
        }

        return new Judgement(first ?? Verdict.Eligible, reasons, first == null ? areaKey : null);
    }

    /// <summary>判定の結果の表示名。</summary>
    public static string Name(Verdict v) => v switch
    {
        Verdict.Eligible => "攻撃可（狩り場の通常個体）",
        Verdict.Unknown => "判別できない",
        Verdict.NotEnemy => "敵ではない",
        Verdict.Dead => "倒れている",
        Verdict.Fate => "FATE",
        Verdict.Event => "イベント個体",
        Verdict.ClaimedByOther => "他人の獲物",
        Verdict.NotHostile => "敵対していない",
        Verdict.NotTargetable => "ターゲット不可",
        Verdict.PlayerBusy => "自分がコンテンツ・リーヴ中",
        Verdict.OutOfGround => "狩り場の範囲外",
        Verdict.NotInPlan => "今回の計画外",
        _ => v.ToString(),
    };
}
