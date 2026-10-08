using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace AutoMobFarm.Data;

/// <summary>
/// 敵に「攻撃が届くか」の判断。
/// 距離を自分で決め打ちしない。ジョブの「敵を狙う GCD（魔法・ウェポンスキル）」をゲームデータから選び、
/// 実際に届くか（射程・視線）はゲームの ActionManager.GetActionInRangeOrLoS に聞く（RSR の TargetHelper.cs と同じ考え方）。
///  ・近接の役割（ClassJob.Role 1＝タンク・2＝近接）：いちばん低いレベルで覚える技（近接の射程）。
///  ・遠隔の役割：覚えている技のうち、いちばん遠くまで届くもの（ゲームの GetActionRange で比べる）。
/// 戻り値の番号（LogMessage で確認）：0＝使える、562＝見えない、565＝向きが違う（向けば使える＝届くに入れる）、566＝射程外。
/// </summary>
public static class AttackReach
{
    public readonly record struct Candidate(uint ActionId, byte Level, sbyte Range);

    public static readonly uint[] ReachableCodes = [0, 565];

    private static readonly Dictionary<uint, List<Candidate>> CandidateCache = [];
    private static readonly Dictionary<(uint Job, int Level), uint> PickCache = [];

    public static bool IsMeleeRole(uint classJob)
        => !Svc.Data.GetExcelSheet<ClassJob>().TryGetRow(classJob, out var row) || row.Role is 1 or 2;

    public static List<Candidate> Candidates(uint classJob)
    {
        if (CandidateCache.TryGetValue(classJob, out var cached))
            return cached;

        var jobs = new HashSet<uint> { classJob };
        if (Svc.Data.GetExcelSheet<ClassJob>().TryGetRow(classJob, out var cj) && cj.ClassJobParent.RowId != 0)
            jobs.Add(cj.ClassJobParent.RowId);

        // ActionCategory 2＝魔法、3＝ウェポンスキル（ゲームデータの ActionCategory の名前で確認）
        var list = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>()
            .Where(a => a.IsPlayerAction && !a.IsPvP && a.CanTargetHostile && a.Range != 0
                        && a.ActionCategory.RowId is 2 or 3 && jobs.Contains(a.ClassJob.RowId))
            .Select(a => new Candidate(a.RowId, a.ClassJobLevel, a.Range))
            .OrderBy(c => c.Level).ThenBy(c => c.ActionId)
            .ToList();

        CandidateCache[classJob] = list;
        return list;
    }

    public static uint Choose(bool melee, IReadOnlyList<Candidate> candidates, int level, Func<uint, float> rangeOf)
    {
        var learned = candidates.Where(c => c.Level <= Math.Max(level, 1)).ToList();
        if (learned.Count == 0)
            return 0;

        if (melee)
            return learned.OrderBy(c => c.Level).ThenBy(c => c.ActionId).First().ActionId;

        return learned
            .Select(c => (c.ActionId, c.Level, Range: rangeOf(c.ActionId)))
            .OrderByDescending(x => x.Range).ThenBy(x => x.Level).ThenBy(x => x.ActionId)
            .First().ActionId;
    }

    public static unsafe uint PickAction(uint classJob, int level)
    {
        if (classJob == 0 || level <= 0)
            return 0;
        if (PickCache.TryGetValue((classJob, level), out var cached))
            return cached;

        uint pick;
        try
        {
            pick = Choose(IsMeleeRole(classJob), Candidates(classJob), level,
                id => FFXIVClientStructs.FFXIV.Client.Game.ActionManager.GetActionRange(id));
        }
        catch (Exception ex)
        {
            Core.DebugLog.Current?.Line("戦闘", $"攻撃の届く範囲の技を選べませんでした（{ex.Message}）。距離で判断します");
            pick = 0;
        }

        PickCache[(classJob, level)] = pick;
        return pick;
    }

    /// <summary>いまの自分から、その敵に攻撃が届くか（射程・視線）。聞けないときは null（呼び出し側が距離で判断する）。</summary>
    public static unsafe bool? InReach(Dalamud.Game.ClientState.Objects.Types.IGameObject target, out uint code)
    {
        code = 0;
        var me = Svc.Objects.LocalPlayer;
        if (me == null || target == null)
            return null;

        var action = PickAction(me.ClassJob.RowId, me.Level);
        if (action == 0)
            return null;

        try
        {
            code = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.GetActionInRangeOrLoS(
                action,
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)me.Address,
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target.Address);
            return ReachableCodes.Contains(code);
        }
        catch (Exception ex)
        {
            Core.DebugLog.Current?.Line("戦闘", $"攻撃が届くかをゲームに聞けませんでした（{ex.Message}）。距離で判断します");
            return null;
        }
    }
}
