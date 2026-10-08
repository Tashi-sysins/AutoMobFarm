using System.Collections.Generic;
using System.Numerics;
using Lumina.Excel.Sheets;

namespace AutoMobFarm.Data;

/// <summary>名前（アイテム・敵・エリア）をゲームのシートから引く（候補データに名前を持たせず、実行時に解決する）。</summary>
public static class GameText
{
    private static readonly Dictionary<uint, string> ItemNames = [];
    private static readonly Dictionary<uint, string> MobNames = [];
    private static readonly Dictionary<uint, string> TerritoryNames = [];
    private static readonly Dictionary<uint, uint> ItemIcons = [];

    /// <summary>旧プリセットなど、カタログに無い品の絵だけをゲームから引く。</summary>
    public static uint ItemIcon(uint id)
    {
        if (!ItemIcons.TryGetValue(id, out var icon))
        {
            icon = Svc.Data.GetExcelSheet<Item>().TryGetRow(id, out var row) ? row.Icon : 0u;
            ItemIcons[id] = icon;
        }
        return icon;
    }

    /// <summary>レシピの材料（品・個数）。Artisan のリストの取り込みで使う（Artisan の Ingredients() と同じ欄）。表に無ければ null。</summary>
    public static IReadOnlyList<(uint Item, int Amount)>? RecipeIngredients(uint recipeId)
    {
        if (!Svc.Data.GetExcelSheet<Recipe>().TryGetRow(recipeId, out var recipe))
            return null;
        var list = new List<(uint, int)>();
        for (var i = 0; i < recipe.Ingredient.Count && i < recipe.AmountIngredient.Count; i++)
            list.Add((recipe.Ingredient[i].RowId, recipe.AmountIngredient[i]));
        return list;
    }

    public static string Item(uint id)
    {
        if (ItemNames.TryGetValue(id, out var s))
            return s;
        s = Svc.Data.GetExcelSheet<Item>().TryGetRow(id, out var row) && row.Name.ExtractText() is { Length: > 0 } n ? n : $"アイテム#{id}";
        ItemNames[id] = s;
        return s;
    }

    public static string Mob(uint nameId)
    {
        if (MobNames.TryGetValue(nameId, out var s))
            return s;
        s = Svc.Data.GetExcelSheet<BNpcName>().TryGetRow(nameId, out var row) && row.Singular.ExtractText() is { Length: > 0 } n ? n : $"敵#{nameId}";
        MobNames[nameId] = s;
        return s;
    }

    public static string Territory(uint id)
    {
        if (TerritoryNames.TryGetValue(id, out var s))
            return s;
        s = Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(id, out var t) && t.PlaceName.ValueNullable?.Name.ExtractText() is { Length: > 0 } n ? n : $"エリア#{id}";
        TerritoryNames[id] = s;
        return s;
    }

    private static readonly Dictionary<uint, string> AetheryteNames = [];
    private static readonly Dictionary<uint, string> ExVersionNames = [];

    /// <summary>エーテライトの名前（Aetheryte.PlaceName）。</summary>
    public static string Aetheryte(uint id)
    {
        if (AetheryteNames.TryGetValue(id, out var s))
            return s;
        s = Svc.Data.GetExcelSheet<Aetheryte>().TryGetRow(id, out var a) && a.PlaceName.ValueNullable?.Name.ExtractText() is { Length: > 0 } n ? n : $"エーテライト#{id}";
        AetheryteNames[id] = s;
        return s;
    }

    /// <summary>拡張の名前（ExVersion.Name。表示言語の名前）。</summary>
    public static string ExVersion(byte id)
    {
        if (id <= 5)
            return CatalogDisplay.ExVersionName(id, string.Empty);
        if (ExVersionNames.TryGetValue(id, out var s))
            return s;
        s = Svc.Data.GetExcelSheet<ExVersion>().TryGetRow(id, out var e) && e.Name.ExtractText() is { Length: > 0 } n ? n : $"拡張#{id}";
        ExVersionNames[id] = s;
        return s;
    }
}

/// <summary>地図座標とワールド座標の変換（Dalamud の MapUtil の式の逆算）。</summary>
public static class MapCoords
{
    /// <summary>ワールド座標（X・Z）を地図座標へ。地図が無いエリアでは null。</summary>
    public static Vector2? ToMap(uint territory, float worldX, float worldZ)
    {
        if (!Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var t) || !t.Map.IsValid || t.Map.RowId == 0)
            return null;

        var map = t.Map.Value;
        if (map.SizeFactor == 0)
            return null;
        var scale = (float)map.SizeFactor;
        float Conv(float v, short offset) => (0.02f * (v + offset)) + (2048f / scale) + 1f;
        return new Vector2(Conv(worldX, map.OffsetX), Conv(worldZ, map.OffsetY));
    }
}
