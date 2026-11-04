using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Replace the layer build-up of a Basic wall type (WallType.SetCompoundStructure):
/// one entry per layer, exterior first, each with a material and a width. Use it
/// on a type duplicated with duplicate_family_type — every wall of the type
/// changes. Curtain/stacked types have no compound structure and are refused.
///
/// Params (units: meters by default; "units":"feet" for imperial):
///   - typeId:   long, required — the WallType to change.
///   - layers:   [ { materialName: string, width: number, function?: string } ], required,
///               exterior → interior. function = Structure | Substrate | Insulation |
///               Finish1 | Finish2 | Membrane (default Structure; a Membrane layer has width 0).
///
/// Returns: { typeId, name, widthMeters, layers: [ { material, width, function } ] }.
/// </summary>
public sealed class SetWallTypeLayersCommand : IRevitCommand
{
    public string Name => "set_wall_type_layers";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var scale = P.Units(p) == "feet" ? 1.0 : P.MetersToFeet;

        var typeId = new ElementId(P.Long(p, "typeId"));
        var wallType = doc.GetElement(typeId) as WallType
            ?? throw new RevitCommandException("not_found", $"Element {typeId.Value} is not a WallType.");
        if (wallType.Kind != WallKind.Basic)
            throw new RevitCommandException("invalid_parameter",
                $"WallType '{wallType.Name}' is {wallType.Kind}; only Basic wall types have layers.");

        var materials = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();
        var layers = new List<CompoundStructureLayer>();
        var echo = new JsonArray();
        var arr = P.Arr(p, "layers");
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonObject l)
                throw new RevitCommandException("bad_request", $"layers[{i}] must be an object.");
            var matName = P.Str(l, "materialName");
            var mat = materials.FirstOrDefault(m => m.Name == matName)
                ?? throw new RevitCommandException("not_found", $"Material '{matName}' not found (see list_materials).");
            var fnName = P.StrOrNull(l, "function") ?? "Structure";
            if (!System.Enum.TryParse<MaterialFunctionAssignment>(fnName, true, out var fn))
                throw new RevitCommandException("invalid_parameter", $"layers[{i}].function '{fnName}' is not a layer function.");
            var width = fn == MaterialFunctionAssignment.Membrane ? 0.0 : P.Dbl(l, "width") * scale;
            layers.Add(new CompoundStructureLayer(width, fn, mat.Id));
            echo.Add(new JsonObject { ["material"] = mat.Name, ["width"] = width / scale, ["function"] = fn.ToString() });
        }
        if (layers.Count == 0)
            throw new RevitCommandException("bad_request", "'layers' must contain at least one layer.");

        var cs = CompoundStructure.CreateSimpleCompoundStructure(layers);
        var structural = layers.FindIndex(l => l.Function == MaterialFunctionAssignment.Structure);
        if (structural >= 0) cs.StructuralMaterialIndex = structural;
        wallType.SetCompoundStructure(cs);

        return new JsonObject
        {
            ["typeId"] = wallType.Id.Value,
            ["name"] = wallType.Name,
            ["widthMeters"] = wallType.Width * P.FeetToMeters,
            ["layers"] = echo,
        };
    }
}
