using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Show or hide a whole category in one view — the check box in the view's
/// Visibility/Graphics dialog.  Needed when a template-derived view ships with
/// e.g. Room Tags switched off, so tags placed by tag_all_in_view are invisible.
/// A view governed by a view template refuses the change (the template owns
/// V/G); that is reported instead of silently doing nothing.
///
/// Params:
///   - viewId:   long, optional (defaults to active view)
///   - category: string, required — category display name, e.g. "Room Tags", "Rooms"
///   - visible:  bool, required
/// </summary>
public sealed class SetCategoryVisibilityCommand : IRevitCommand
{
    public string Name => "set_category_visibility";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var view = SetViewDetailLevelCommand.ResolveView(doc, ctx, p);
        var categoryName = P.Str(p, "category");
        var visible = P.BoolFrom(p["visible"], "visible");

        var cat = doc.Settings.Categories.Cast<Category>()
            .FirstOrDefault(c => string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase))
            ?? throw new RevitCommandException("not_found",
                $"Category '{categoryName}' not found. Use list_categories to see valid names.");

        if (!view.CanCategoryBeHidden(cat.Id))
            throw new RevitCommandException("bad_request",
                $"Category '{cat.Name}' cannot be shown/hidden in view {view.Id.Value} " +
                "(the view may be controlled by a view template).");

        var before = !view.GetCategoryHidden(cat.Id);
        view.SetCategoryHidden(cat.Id, !visible);

        return new JsonObject
        {
            ["viewId"] = view.Id.Value,
            ["view"] = view.Name,
            ["category"] = cat.Name,
            ["visibleBefore"] = before,
            ["visibleAfter"] = !view.GetCategoryHidden(cat.Id),
        };
    }
}
