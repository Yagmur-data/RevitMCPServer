using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a free-standing Railing element (Autodesk.Revit.DB.Architecture.Railing)
/// along a path — not hosted on a stair or floor edge, usable for balconies,
/// terraces, or a guard rail beside an opening.
///
/// Parameters (units: meters by default; pass "units":"feet" for imperial):
///   - path:            [{x,y,z?}, ...]  required, at least 2 points (an open polyline)
///   - levelName:       string           optional, defaults to lowest level
///   - railingTypeName: string           optional, defaults to first RailingType
///   - units:           "meters"|"feet"  optional, default "meters"
/// </summary>
public sealed class CreateRailingCommand : IRevitCommand
{
    public string Name => "create_railing";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var pts = P.XyzList(p, "path", units);
        if (pts.Count < 2)
            throw new RevitCommandException("invalid_parameter", "Railing path must have at least 2 points.");

        var level = CreateWallCommand.ResolveLevel(doc, P.StrOrNull(p, "levelName"));
        var railingType = ResolveRailingType(doc, P.StrOrNull(p, "railingTypeName"));

        var curveLoop = new CurveLoop();
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var a = new XYZ(pts[i].X, pts[i].Y, level.Elevation);
            var b = new XYZ(pts[i + 1].X, pts[i + 1].Y, level.Elevation);
            if (a.DistanceTo(b) < 1e-6)
                throw new RevitCommandException("invalid_parameter", $"Path segment {i} has zero length.");
            curveLoop.Append(Line.CreateBound(a, b));
        }

        var railing = Railing.Create(doc, curveLoop, railingType.Id, level.Id);

        return new JsonObject
        {
            ["id"] = railing.Id.Value,
            ["levelName"] = level.Name,
            ["railingTypeName"] = railingType.Name,
            ["pointCount"] = pts.Count,
        };
    }

    private static RailingType ResolveRailingType(Document doc, string? name)
    {
        var query = new FilteredElementCollector(doc).OfClass(typeof(RailingType)).Cast<RailingType>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return query.FirstOrDefault()
                ?? throw new RevitCommandException("not_found", "No RailingType exists in the document.");
        }
        return query.FirstOrDefault(t => t.Name == name)
            ?? throw new RevitCommandException("not_found", $"RailingType '{name}' not found.");
    }
}
