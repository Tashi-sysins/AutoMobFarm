using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoMobFarm.Data;

/// <summary>
/// モブ用カタログのパック（埋め込みリソース。出典は THIRD_PARTY_NOTICES.md）。
/// 3タブの画面（モブ・アイテム・自動討伐）と狩り場の範囲の元データ。個体へ仕掛けるかは狩り場と現地の判定の両方で決める。
/// 高さは持たない。レベルは出どころ付きの集合（空なら不明）。青魔法・捕獲は保存済みの出典の対応だけを読む。
/// </summary>
public sealed class MobCatalog
{
    public const string ExpectedFormat = "automobfarm-mob-catalog/2";

    [JsonPropertyName("format")]
    public string Format { get; init; } = string.Empty;

    [JsonPropertyName("meta")]
    public CatalogMeta Meta { get; init; } = new();

    [JsonPropertyName("territories")]
    public List<CatalogTerritory> Territories { get; init; } = [];

    [JsonPropertyName("items")]
    public List<CatalogItem> Items { get; init; } = [];

    [JsonPropertyName("mobs")]
    public List<CatalogMob> Mobs { get; init; } = [];

    [JsonPropertyName("points")]
    public List<CatalogPoint> Points { get; init; } = [];

    private Dictionary<uint, CatalogMob>? mobByName;
    private Dictionary<uint, List<CatalogPoint>>? pointsByName;
    private Dictionary<uint, List<uint>>? mobsByItem;
    private Dictionary<uint, byte>? exByTerritory;
    private Dictionary<uint, string>? itemNames;
    private Dictionary<uint, uint>? itemIcons;
    private Dictionary<string, CatalogPoint>? pointByKey;

    public CatalogMob? Mob(uint nameId) => this.mobByName!.GetValueOrDefault(nameId);

    /// <summary>その名前の地点（要確認を含む。除外はパックに入っていない）。</summary>
    public IReadOnlyList<CatalogPoint> PointsOf(uint nameId) => this.pointsByName!.GetValueOrDefault(nameId) ?? [];

    /// <summary>その品を落とすモブ（名前 ID）。</summary>
    public IReadOnlyList<uint> MobsDropping(uint itemId) => this.mobsByItem!.GetValueOrDefault(itemId) ?? [];

    /// <summary>エリアの拡張（TerritoryType.ExVersion。分からなければ null）。</summary>
    public byte? ExVersion(uint territory) => this.exByTerritory!.TryGetValue(territory, out var ex) ? ex : null;

    /// <summary>品の名前（パックに入っている日本語名。無ければ null：画面はゲームの表から引き直す）。</summary>
    public string? ItemName(uint itemId) => this.itemNames!.GetValueOrDefault(itemId);

    public uint ItemIcon(uint itemId) => this.itemIcons!.GetValueOrDefault(itemId);

    public CatalogPoint? Point(string key) => this.pointByKey!.GetValueOrDefault(key);

    public static MobCatalog LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream("AutoMobFarm.mob_catalog_pack.json")
                      ?? throw new InvalidOperationException("埋め込みのモブ用カタログ（mob_catalog_pack.json）が見つかりません");
        return Load(s);
    }

    public static MobCatalog Load(Stream s)
    {
        var c = JsonSerializer.Deserialize<MobCatalog>(s) ?? throw new InvalidOperationException("モブ用カタログを読めません");
        if (c.Format != ExpectedFormat)
            throw new InvalidOperationException($"モブ用カタログの形式が違います（{c.Format} / 期待 {ExpectedFormat}）");
        foreach (var p in c.Points)
        {
            // パックを作る台本で確かめているが、読む側でも、作り物の値（範囲外のレベル）を受け入れない
            if (p.Levels.Any(l => l is < 1 or > 100))
                throw new InvalidOperationException($"地点 {p.Key} のレベルが 1〜100 の外です");
        }

        c.mobByName = c.Mobs.ToDictionary(m => m.NameId);
        c.pointsByName = c.Points.GroupBy(p => p.NameId).ToDictionary(g => g.Key, g => g.ToList());
        c.mobsByItem = c.Mobs.SelectMany(m => m.Items.Select(i => (Item: i, m.NameId))).GroupBy(x => x.Item)
            .ToDictionary(g => g.Key, g => g.Select(x => x.NameId).Distinct().OrderBy(n => n).ToList());
        c.exByTerritory = c.Territories.ToDictionary(t => t.Territory, t => t.ExVersion);
        c.itemNames = c.Items.Where(i => i.Name != null).ToDictionary(i => i.Id, i => i.Name!);
        c.itemIcons = c.Items.ToDictionary(i => i.Id, i => i.Icon);
        c.pointByKey = c.Points.ToDictionary(p => p.Key);
        return c;
    }
}

public sealed class CatalogMeta
{
    [JsonPropertyName("catalog_schema")]
    public string CatalogSchema { get; init; } = string.Empty;

    [JsonPropertyName("catalog_generated_at")]
    public string GeneratedAt { get; init; } = string.Empty;

    [JsonPropertyName("game_version")]
    public string GameVersion { get; init; } = string.Empty;

    [JsonPropertyName("points_in_catalog")]
    public int PointsInCatalog { get; init; }

    [JsonPropertyName("teamcraft_clusters")]
    public int TeamcraftClusters { get; init; }

    [JsonPropertyName("excluded_points")]
    public Dictionary<string, int> ExcludedPoints { get; init; } = [];

    [JsonPropertyName("excluded_crystal_mobs")]
    public int ExcludedCrystalMobs { get; init; }

    [JsonPropertyName("level_sources")]
    public Dictionary<string, int> LevelSources { get; init; } = [];

    [JsonPropertyName("blue_magic")]
    public string BlueMagic { get; init; } = string.Empty;

    [JsonPropertyName("capture")]
    public string Capture { get; init; } = string.Empty;

    [JsonPropertyName("note")]
    public string Note { get; init; } = string.Empty;
}

public sealed class CatalogTerritory
{
    [JsonPropertyName("t")]
    public uint Territory { get; init; }

    [JsonPropertyName("ex")]
    public byte ExVersion { get; init; }
}

public sealed class CatalogItem
{
    [JsonPropertyName("icon")]
    public uint Icon { get; init; }

    [JsonPropertyName("id")]
    public uint Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class CatalogMob
{
    [JsonPropertyName("icon")]
    public uint Icon { get; init; }

    [JsonPropertyName("blu")]
    public List<CatalogBlueMagic> BlueMagic { get; init; } = [];

    [JsonPropertyName("bst")]
    public List<CatalogCapture> Capture { get; init; } = [];

    [JsonPropertyName("n")]
    public uint NameId { get; init; }

    /// <summary>日本語名が FATE 名の「」の中と同じ（要確認の手がかり）。</summary>
    [JsonPropertyName("fate")]
    public bool FateNameMatch { get; init; }

    [JsonPropertyName("items")]
    public List<uint> Items { get; init; } = [];

    /// <summary>種類 ID の候補（出現記録・Teamcraft）。1対1にしない。</summary>
    [JsonPropertyName("bases")]
    public List<uint> BaseIds { get; init; } = [];
}

/// <summary>地点（補助データの1地点、または Teamcraft の地点を地図座標 1.0 の升目でまとめたもの）。</summary>
public sealed class CatalogPoint
{
    /// <summary>安定キー（補助 ls:…／Teamcraft の升目 tcg:…）。</summary>
    [JsonPropertyName("k")]
    public string Key { get; init; } = string.Empty;

    /// <summary>出典：ls＝LuminaSupplemental の MobSpawn、tc＝Teamcraft。</summary>
    [JsonPropertyName("src")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("n")]
    public uint NameId { get; init; }

    [JsonPropertyName("b")]
    public uint? BaseId { get; init; }

    [JsonPropertyName("t")]
    public uint Territory { get; init; }

    [JsonPropertyName("m")]
    public uint MapId { get; init; }

    [JsonPropertyName("mx")]
    public float? MapX { get; init; }

    [JsonPropertyName("my")]
    public float? MapY { get; init; }

    /// <summary>ワールド X（派生。地図の縮尺が分からなければ null）。</summary>
    [JsonPropertyName("wx")]
    public float? WorldX { get; init; }

    [JsonPropertyName("wz")]
    public float? WorldZ { get; init; }

    /// <summary>戦闘レベルの観測値の集合（空＝不明）。</summary>
    [JsonPropertyName("lv")]
    public List<int> Levels { get; init; } = [];

    /// <summary>tp＝地点・tn＝近く・tt＝同エリアの実測、gz＝Garland の参考値。</summary>
    [JsonPropertyName("ls")]
    public string? LevelSource { get; init; }

    public bool ReferenceLevels => this.LevelSource == "gz";

    /// <summary>分類（field_candidate_unverified・external_candidate_unverified・review_required_*）。</summary>
    [JsonPropertyName("s")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("r")]
    public List<string> Reasons { get; init; } = [];

    /// <summary>まとめた元の行・地点の数。</summary>
    [JsonPropertyName("c")]
    public int Count { get; init; }

    /// <summary>要確認の地点か（クエストの置き場所と重なる・FATE 名と同名など）。</summary>
    public bool NeedsReview => this.Status.StartsWith("review", StringComparison.Ordinal);

    public string SourceName => this.Source == "tc" ? "Teamcraft の候補" : "補助データの候補";
}

/// <summary>パックでは [番号, 技名] の短い配列。使う側は名前付きの値として扱う。</summary>
[JsonConverter(typeof(CatalogBlueMagicConverter))]
public sealed record CatalogBlueMagic(int No, string Action);

public sealed class CatalogBlueMagicConverter : JsonConverter<CatalogBlueMagic>
{
    public override CatalogBlueMagic Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var row = doc.RootElement;
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 2)
            throw new JsonException("青魔法は [番号, 技名] の配列です");
        return new CatalogBlueMagic(row[0].GetInt32(), row[1].GetString() ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, CatalogBlueMagic value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.No);
        writer.WriteStringValue(value.Action);
        writer.WriteEndArray();
    }
}

/// <summary>捕まえる場所の文も残す。同名の敵が野外にいても、その場所で捕まえられるとは断定しない。</summary>
public sealed class CatalogCapture
{
    [JsonPropertyName("no")]
    public int No { get; init; }

    [JsonPropertyName("sp")]
    public string Species { get; init; } = string.Empty;

    [JsonPropertyName("icon")]
    public uint Icon { get; init; }

    [JsonPropertyName("k")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("w")]
    public string Where { get; init; } = string.Empty;

    [JsonPropertyName("lo")]
    public int? LevelMin { get; init; }

    [JsonPropertyName("hi")]
    public int? LevelMax { get; init; }
}
