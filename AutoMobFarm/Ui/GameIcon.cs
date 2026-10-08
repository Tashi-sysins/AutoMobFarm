using System;
using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using EstellUtils.UI;

namespace AutoMobFarm.Ui;

/// <summary>
/// ゲームの絵を行の高さで描く（EstellUtils の Image）。絵が無い敵や読み込み待ちは、同じ大きさの空白にする
/// （EUi.Image はテクスチャが null でも領域を取るので、名前の位置がそろう）。
/// 横並び（HStack・Cell・SelectableRow）の中で、名前の前に置く。
/// </summary>
public static class GameIcon
{
    /// <summary>絵の大きさ（今のフォントの行の高さ）。</summary>
    public static float Size => EUi.LineHeight;

    public static void Draw(uint icon) => Draw(icon, Size);

    public static void Draw(uint icon, float size) => EUi.Image(Wrap(icon), new Vector2(size, size));

    /// <summary>
    /// 決まった矩形の左端に、行の高さの四角で絵を描く（場所は取らない。EUi.InlineList の drawPrefix から使う）。絵が無ければ何も描かない。
    /// </summary>
    public static void DrawAt(uint icon, EstellUtils.UI.Core.Rect rect)
    {
        var size = MathF.Min(Size, rect.Height);
        EUi.ImageAt(Wrap(icon), EstellUtils.UI.Core.Rect.FromSize(new Vector2(rect.Min.X, rect.Center.Y - (size * 0.5f)), new Vector2(size, size)));
    }

    private static IDalamudTextureWrap? Wrap(uint icon)
        => icon != 0 && Svc.Texture.GetFromGameIcon(new GameIconLookup(icon)).TryGetWrap(out var w, out _) ? w : null;
}
