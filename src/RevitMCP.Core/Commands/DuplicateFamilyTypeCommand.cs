using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Duplicate a type under a new name: a loadable family type (FamilySymbol) or a
/// system family type (any other ElementType — railing, stair, wall, floor, roof
/// types).  Set parameters on the new type afterwards with set_parameter (using
/// the returned typeId), which already handles unit conversion.
///
/// Params:
///   - sourceTypeId: long, required — ElementId of the type to copy.
///   - newName:      string, required — name for the new type (must be unique in the family).
///
/// Returns: { typeId, name, familyName }.
/// </summary>
public sealed class DuplicateFamilyTypeCommand : IRevitCommand
{
    public string Name => "duplicate_family_type";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var srcId = new ElementId(P.Long(p, "sourceTypeId"));
        var src = doc.GetElement(srcId) as ElementType
            ?? throw new RevitCommandException("not_found",
                $"Element {srcId.Value} is not a type (ElementType).");

        var newName = P.Str(p, "newName");

        var dup = src.Duplicate(newName)
            ?? throw new RevitCommandException("command_failed",
                "Duplicate did not return a type.");

        return new JsonObject
        {
            ["typeId"] = dup.Id.Value,
            ["name"] = dup.Name,
            ["familyName"] = dup.FamilyName,
        };
    }
}
