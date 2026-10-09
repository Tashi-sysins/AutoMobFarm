using System;
using System.Collections.Generic;

namespace AutoMobFarm.Ui;

/// <summary>
/// 一覧をつまんで並べ替えるときの位置の計算（プリセットの一覧）。画面にもゲームにも触らないので、試験で同じものを動かす。
/// 行は画面の上から順の (上端, 下端)。差し込み位置は 0（先頭の上）〜行の数（末尾の下）。
/// </summary>
public static class ListReorder
{
    /// <summary>マウスの高さから差し込み位置を決める（行の上半分なら、その行の上・下半分なら、その行の下）。一覧の上より上は先頭・下より下は末尾。</summary>
    public static int InsertIndex(IReadOnlyList<(float Top, float Bottom)> rows, float mouseY)
    {
        for (var i = 0; i < rows.Count; i++)
            if (mouseY < (rows[i].Top + rows[i].Bottom) * 0.5f)
                return i;
        return rows.Count;
    }

    /// <summary>
    /// 細い横線を引く高さ（行と行のすき間の中ほど）。先頭の上・末尾の下は、送りの枠で切れないよう行の内側へ 1px 寄せる。
    /// </summary>
    public static float LineY(IReadOnlyList<(float Top, float Bottom)> rows, int insertAt)
    {
        if (rows.Count == 0)
            return 0f;
        if (insertAt <= 0)
            return rows[0].Top + 1f;
        if (insertAt >= rows.Count)
            return rows[^1].Bottom - 1f;
        return MathF.Round((rows[insertAt - 1].Bottom + rows[insertAt].Top) * 0.5f);
    }
}
