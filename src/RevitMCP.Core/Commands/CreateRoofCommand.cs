using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a flat or uniformly-sloped FootPrintRoof from a closed polygonal
/// profile — a real RoofBase element (Autodesk.Revit.DB.FootPrintRoof), not
/// a floor slab standing in for a roof.
///
/// Parameters (units: meters by default; pass "units":"feet" for imperial):
///   - profile:       [{x,y,z?}, ...]  required, at least 3 points, closed automatically
///   - levelName:     string           optional, defaults to the highest level
///   - roofTypeName:  string           optional, defaults to first RoofType
///   - slopeAngleDeg: number           optional, default 0 (flat) — applied to every edge
///   - edgeSlopesDeg: (number|null)[]  optional, one entry per profile edge (edge i runs
///                                     profile[i] -> profile[i+1], last edge closes back to
///                                     profile[0]); null or 0 = edge does not define slope
///                                     (gable end). Overrides slopeAngleDeg when given.
///   - units:         "meters"|"feet"  optional, default "meters"
/// </summary>
public sealed class CreateRoofCommand : IRevitCommand
{
    public string Name => "create_roof";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var pts = P.XyzList(p, "profile", units);
        if (pts.Count < 3)
            throw new RevitCommandException("invalid_parameter", "Roof profile must have at least 3 points.");

        var level = ResolveTopLevel(doc, P.StrOrNull(p, "levelName"));
        var roofType = ResolveRoofType(doc, P.StrOrNull(p, "roofTypeName"));
        var slopeDeg = P.DblOr(p, "slopeAngleDeg", 0.0);
        var edgeSlopes = P.NullableDblListOrNull(p, "edgeSlopesDeg");
        if (edgeSlopes is not null && edgeSlopes.Count != pts.Count)
            throw new RevitCommandException("invalid_parameter",
                $"edgeSlopesDeg must have one entry per profile edge ({pts.Count}), got {edgeSlopes.Count}.");
        var slopes = edgeSlopes ?? Enumerable.Repeat<double?>(slopeDeg, pts.Count).ToList();
        foreach (var s in slopes)
        {
            if (s is < 0 or >= 90)
                throw new RevitCommandException("invalid_parameter", $"Slope {s} deg is outside 0..<90.");
        }

        // The official SDK sample builds this via application.Create.NewCurveArray(),
        // not a bare `new CurveArray()` — NewFootPrintRoof threw a mysterious
        // ArgumentNullException (empty ParamName) when fed a bare-constructed one.
        var footprint = doc.Application.Create.NewCurveArray();
        for (var i = 0; i < pts.Count; i++)
        {
            var a = new XYZ(pts[i].X, pts[i].Y, level.Elevation);
            var bRaw = pts[(i + 1) % pts.Count];
            var b = new XYZ(bRaw.X, bRaw.Y, level.Elevation);
            if (a.DistanceTo(b) < 1e-6)
                throw new RevitCommandException("invalid_parameter", $"Profile segment {i} has zero length.");
            footprint.Append(Line.CreateBound(a, b));
        }

        FootPrintRoof roof;
        // Must be pre-allocated like the SDK sample does: the out-parameter is a
        // C++/CLI tracking reference the native side reads, and a null here was
        // the source of the empty-ParamName ArgumentNullException.
        var modelCurves = new ModelCurveArray();
        try
        {
            roof = doc.Create.NewFootPrintRoof(footprint, level, roofType, out modelCurves);
        }
        catch (Exception ex) when (
            ex is System.ArgumentNullException
            || ex is Autodesk.Revit.Exceptions.ArgumentNullException)
        {
            var paramName = (ex as System.ArgumentNullException)?.ParamName
                ?? (ex as Autodesk.Revit.Exceptions.ArgumentNullException)?.ParamName;
            var activeViewType = ctx.App.ActiveUIDocument?.ActiveView?.ViewType.ToString() ?? "(none)";
            throw new RevitCommandException("command_failed",
                $"NewFootPrintRoof rejected argument '{paramName ?? "(unknown)"}' ({ex.GetType().FullName}) as null. " +
                $"Diagnostics: footprintCurveCount={footprint.Size}, levelName={level.Name}, " +
                $"levelId={level.Id.Value}, roofTypeName={roofType.Name}, roofTypeId={roofType.Id.Value}, " +
                $"activeViewType={activeViewType}.");
        }

        // footPrintToModelCurvesMapping is index-aligned with the footprint curves.
        var appliedSlopes = new JsonArray();
        for (var i = 0; i < modelCurves.Size; i++)
        {
            var mc = modelCurves.get_Item(i);
            var s = i < slopes.Count ? slopes[i] : null;
            var definesSlope = s is > 0;
            roof.set_DefinesSlope(mc, definesSlope);
            if (definesSlope)
                roof.set_SlopeAngle(mc, s!.Value * System.Math.PI / 180.0);
            appliedSlopes.Add(definesSlope ? JsonValue.Create(s!.Value) : null);
        }

        return new JsonObject
        {
            ["id"] = roof.Id.Value,
            ["levelName"] = level.Name,
            ["roofTypeName"] = roofType.Name,
            ["pointCount"] = pts.Count,
            ["slopeAngleDeg"] = slopeDeg,
            ["edgeSlopesDeg"] = appliedSlopes,
        };
    }

    private static Level ResolveTopLevel(Document doc, string? name)
    {
        var query = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
        if (!string.IsNullOrWhiteSpace(name))
        {
            return query.FirstOrDefault(l => l.Name == name)
                ?? throw new RevitCommandException("not_found", $"Level '{name}' not found.");
        }
        return query.OrderByDescending(l => l.Elevation).FirstOrDefault()
            ?? throw new RevitCommandException("not_found", "No Level exists in the document.");
    }

    private static RoofType ResolveRoofType(Document doc, string? name)
    {
        var query = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return query.FirstOrDefault()
                ?? throw new RevitCommandException("not_found", "No RoofType exists in the document.");
        }
        return query.FirstOrDefault(t => t.Name == name)
            ?? throw new RevitCommandException("not_found", $"RoofType '{name}' not found.");
    }
}
