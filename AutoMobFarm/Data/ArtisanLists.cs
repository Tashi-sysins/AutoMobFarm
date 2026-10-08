using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AutoMobFarm.Data;

/// <summary>Artisan のリストの1行（レシピ・作る回数・飛ばす印）。</summary>
public sealed record ArtisanListItem(uint RecipeId, int Quantity, bool Skipping);

/// <summary>Artisan の Crafting List（NewCraftingLists の1件）。</summary>
public sealed record ArtisanList(int Id, string Name, IReadOnlyList<ArtisanListItem> Recipes);

/// <summary>取り込みの結果：敵が落とす素材（プリセットに入れる）・それ以外の素材・ゲームの表に無いレシピ。</summary>
public sealed record ArtisanMaterials(
    IReadOnlyList<(uint ItemId, int Count)> MobDrops,
    IReadOnlyList<(uint ItemId, int Count)> Others,
    IReadOnlyList<uint> UnknownRecipes);

/// <summary>
/// Artisan の Crafting Lists から、敵が落とす素材だけを取り出す（プリセットを作るため）。
///  ・Artisan の設定ファイル（pluginConfigs\Artisan.json）を読むだけ。書かない・Artisan の動きに触らない。
///  ・素材の数は Artisan の ListMaterials（Artisan-GBR\Artisan\CraftingList\CraftingList.cs）と同じ数え方：
///    リストの各レシピ（飛ばす印・回数 0 を除く）の材料の個数 × 作る回数を足す。中間素材の下の材料までは辿らない
///    （Artisan のリストには、ふつう中間素材のレシピも行として入っている）。
///  ・敵が落とす素材＝埋め込みのカタログの品（普段から湧く敵が落とす品）。それ以外は外して、数だけ返す。
///  ・ゲームの表（Recipe）を引く部分は呼ぶ側が渡す（試験では偽物の表を使う）。
/// </summary>
public static class ArtisanLists
{
    /// <summary>自分の設定フォルダー（pluginConfigs\AutoMobFarm）から、Artisan の設定ファイルの場所を作る。</summary>
    public static string PathFrom(string myConfigDir)
        => Path.Combine(Directory.GetParent(myConfigDir.TrimEnd('\\', '/'))?.FullName ?? myConfigDir, "Artisan.json");

    /// <summary>Artisan の設定からリストを読む。読めなければ空の一覧と理由。Artisan が書き込み中でも開けるよう共有で開く。</summary>
    public static (List<ArtisanList> Lists, string? Error) Read(string path)
    {
        if (!File.Exists(path))
            return ([], $"Artisan の設定が見つかりません（{path}）");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            return Parse(doc.RootElement);
        }
        catch (Exception ex)
        {
            return ([], $"Artisan の設定を読めません：{ex.Message}");
        }
    }

    /// <summary>設定の JSON（の根）からリストを取り出す。欄が無い・形が違う行は飛ばす。</summary>
    public static (List<ArtisanList> Lists, string? Error) Parse(JsonElement root)
    {
        if (!root.TryGetProperty("NewCraftingLists", out var lists) || lists.ValueKind != JsonValueKind.Array)
            return ([], "Artisan の設定に Crafting Lists（NewCraftingLists）がありません");
        var result = new List<ArtisanList>();
        foreach (var list in lists.EnumerateArray())
        {
            var recipes = new List<ArtisanListItem>();
            if (list.TryGetProperty("Recipes", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (!row.TryGetProperty("ID", out var id) || !id.TryGetUInt32(out var recipeId)
                        || !row.TryGetProperty("Quantity", out var qty) || !qty.TryGetInt32(out var quantity))
                        continue;
                    var skipping = row.TryGetProperty("ListItemOptions", out var options) && options.ValueKind == JsonValueKind.Object
                        && options.TryGetProperty("Skipping", out var skip) && skip.ValueKind == JsonValueKind.True;
                    recipes.Add(new ArtisanListItem(recipeId, quantity, skipping));
                }
            }

            var listId = list.TryGetProperty("ID", out var lid) && lid.TryGetInt32(out var n) ? n : 0;
            var name = list.TryGetProperty("Name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() ?? string.Empty : string.Empty;
            result.Add(new ArtisanList(listId, name, recipes));
        }

        return (result, null);
    }

    /// <summary>
    /// リストの素材の数（Artisan の ListMaterials と同じ数え方）。ingredientsOf はレシピの材料（品・個数）を返す。
    /// 表に無いレシピは数えず、unknownRecipes に入れる。
    /// </summary>
    public static Dictionary<uint, long> Materials(ArtisanList list, Func<uint, IReadOnlyList<(uint Item, int Amount)>?> ingredientsOf,
        List<uint>? unknownRecipes = null)
    {
        var total = new Dictionary<uint, long>();
        foreach (var row in list.Recipes)
        {
            if (row.Skipping || row.Quantity <= 0)
                continue;
            var ingredients = ingredientsOf(row.RecipeId);
            if (ingredients == null)
            {
                unknownRecipes?.Add(row.RecipeId);
                continue;
            }

            foreach (var (item, amount) in ingredients)
            {
                if (item == 0 || amount <= 0)
                    continue;
                total[item] = total.GetValueOrDefault(item) + ((long)amount * row.Quantity);
            }
        }

        return total;
    }

    /// <summary>素材を「敵が落とす（カタログの品）」と「それ以外」に分ける。数は 1〜9999 に収める。品の番号の順。</summary>
    public static ArtisanMaterials Split(IReadOnlyDictionary<uint, long> materials, Func<uint, bool> isMobDrop, IReadOnlyList<uint>? unknownRecipes = null)
    {
        var drops = new List<(uint, int)>();
        var others = new List<(uint, int)>();
        foreach (var (item, count) in materials.OrderBy(kv => kv.Key))
            (isMobDrop(item) ? drops : others).Add((item, (int)Math.Clamp(count, 1, 9999)));
        return new ArtisanMaterials(drops, others, unknownRecipes ?? []);
    }

    /// <summary>敵が落とす素材の候補（その品を落とす敵 × 地点のあるエリア）。自動討伐の候補と同じ形。</summary>
    public static List<Farm.SourceMobRef> SourcesOf(MobCatalog c, uint itemId) => CatalogQuery.SourcesOf(c, itemId);
}
