using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Excel.Sheets;

namespace AutoMobFarm.Data;

/// <summary>
/// エーテライトの位置（テレポ先を「狩り場に一番近いエーテライト」にするため）。
/// Aetheryte 表の Level の行はいまの Level シートにほとんど無いので、地図の印（MapMarker。本体は DataType 3）から求める。高さは分からない。
/// </summary>
public static class AetherytePlaces
{
    private static readonly object Gate = new();
    private static Dictionary<uint, Vector2>? byMarker;

    /// <summary>エーテライトの水平の位置（X・Z）。求められなければ null。</summary>
    public static Vector2? Of(uint aetheryteId)
    {
        if (!Svc.Data.GetExcelSheet<Aetheryte>().TryGetRow(aetheryteId, out var a))
            return null;
        var lv = a.Level.FirstOrDefault(l => l.RowId != 0);
        if (lv.RowId != 0 && Svc.Data.GetExcelSheet<Level>().TryGetRow(lv.RowId, out var row))
            return new Vector2(row.X, row.Z);
        return Markers().TryGetValue(aetheryteId, out var flat) ? flat : null;
    }

    private static Dictionary<uint, Vector2> Markers()
    {
        lock (Gate)
        {
            if (byMarker != null)
                return byMarker;

            var aetherytes = Svc.Data.GetExcelSheet<Aetheryte>();
            var markers = Svc.Data.GetSubrowExcelSheet<MapMarker>();
            var result = new Dictionary<uint, Vector2>();
            foreach (var map in Svc.Data.GetExcelSheet<Map>())
            {
                if (map.MapMarkerRange == 0 || map.SizeFactor == 0 || !markers.TryGetRow(map.MapMarkerRange, out var group))
                    continue;
                foreach (var marker in group)
                {
                    if (marker.DataType != 3)
                        continue;
                    var id = marker.DataKey.RowId;
                    if (id == 0)
                        continue;

                    var scale = map.SizeFactor / 100f;
                    var world = new Vector2(((marker.X - 1024f) / scale) - map.OffsetX, ((marker.Y - 1024f) / scale) - map.OffsetY);
                    var preferred = map.TerritoryType.RowId != 0 && aetherytes.TryGetRow(id, out var owner) && map.TerritoryType.RowId == owner.Territory.RowId;
                    if (preferred || !result.ContainsKey(id))
                        result[id] = world;
                }
            }

            byMarker = result;
            return result;
        }
    }
}
