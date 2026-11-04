using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Set the visual style of a view — the same switch as the Graphic Display
/// Options at the bottom of the Revit canvas (Wireframe / HLR / Shaded /
/// Shaded with Edges / Realistic / Ray Trace). Realistic renders instantly
/// with real material appearances and is what makes get_view_image exports
/// look photoreal-ish instead of a flat line drawing; Ray Trace is Revit's
/// actual path-traced renderer but converges progressively in the live
/// viewport, so a single headless export right after switching to it may
/// still look noisy — Realistic is the reliable choice for exports.
///
/// Params:
///   - viewId: long, optional (defaults to active view)
///   - style:  "Wireframe"|"HLR"|"Shading"|"ShadingWithEdges"|"Realistic"|"RayTrace"
/// </summary>
public sealed class SetViewDisplayStyleCommand : IRevitCommand
{
    public string Name => "set_view_display_style";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var view = SetViewDetailLevelCommand.ResolveView(doc, ctx, p);
        var styleStr = P.Str(p, "style");
        if (!Enum.TryParse<DisplayStyle>(styleStr, true, out var style))
            throw new RevitCommandException("invalid_parameter",
                $"Unknown display style '{styleStr}'. Use Wireframe, HLR, Shading, ShadingWithEdges, Realistic, or RayTrace.");

        view.DisplayStyle = style;

        return new JsonObject
        {
            ["viewId"] = view.Id.Value,
            ["name"] = view.Name,
            ["displayStyle"] = view.DisplayStyle.ToString(),
        };
    }
}
