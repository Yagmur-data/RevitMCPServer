using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Set the vertical (3D) extents of grids — how far each grid line runs up and
/// down in elevations and sections, and so where its bubbles sit.  Grids drawn
/// in plan take their height from the levels that existed at the time, so after
/// a building grows the bubbles can land on top of the upper windows; this
/// moves them above the roof in every elevation at once (Grid.SetVerticalExtents).
///
/// Params:
///   - ids:    long[], optional — grid ids; omitted = every grid in the model
///   - bottom: double, required — lower end (absolute elevation)
///   - top:    double, required — upper end (absolute elevation)
///   - units:  "meters" (default) | "feet"
///
/// Returns: { updated, grids: [{ id, name }] }.
/// </summary>
public sealed class SetGridVerticalExtentsCommand : IRevitCommand
{
    public string Name => "set_grid_vertical_extents";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var scale = P.Units(p) == "feet" ? 1.0 : P.MetersToFeet;
        var bottom = P.Dbl(p, "bottom") * scale;
        var top = P.Dbl(p, "top") * scale;
        if (bottom >= top)
            throw new RevitCommandException("invalid_parameter", "'bottom' must be below 'top'.");

        IEnumerable<Grid> grids;
        if (p["ids"] is JsonArray ids)
        {
            var list = new List<Grid>();
            for (var i = 0; i < ids.Count; i++)
            {
                var id = P.LongFrom(ids[i], $"ids[{i}]");
                list.Add(doc.GetElement(new ElementId(id)) as Grid
                    ?? throw new RevitCommandException("not_found", $"Element {id} is not a grid."));
            }
            grids = list;
        }
        else
        {
            grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
        }

        var updated = new JsonArray();
        foreach (var grid in grids)
        {
            grid.SetVerticalExtents(bottom, top);
            updated.Add(new JsonObject { ["id"] = grid.Id.Value, ["name"] = grid.Name });
        }

        return new JsonObject
        {
            ["updated"] = updated.Count,
            ["grids"] = updated,
        };
    }
}
