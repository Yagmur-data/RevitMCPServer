using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// TEMPORARY diagnostic command — reflects over the actual loaded RevitAPI
/// assembly to list every real, currently-available way to create a roof in
/// THIS exact Revit version, instead of guessing from documentation that may
/// target a different version. Remove once the real create_roof is fixed.
/// </summary>
public sealed class DebugRoofApiCommand : IRevitCommand
{
    public string Name => "debug_roof_api";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var result = new JsonObject();

        // Every public static method on any type whose name contains "Roof".
        var roofTypes = typeof(Document).Assembly.GetTypes()
            .Where(t => t.Name.Contains("Roof", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.FullName)
            .ToList();
        var typesArr = new JsonArray();
        foreach (var t in roofTypes)
            typesArr.Add(t.FullName);
        result["roofRelatedTypes"] = typesArr;

        var staticMethodsArr = new JsonArray();
        foreach (var t in roofTypes)
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.Name is "Equals" or "ReferenceEquals" or "GetType") continue;
                var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                staticMethodsArr.Add($"{t.FullName}.{m.Name}({ps}) : {m.ReturnType.Name}");
            }
        }
        result["staticFactoryMethods"] = staticMethodsArr;

        // Instance methods on Autodesk.Revit.Creation.Document (doc.Create) mentioning Roof.
        var creationDocType = typeof(Autodesk.Revit.Creation.Document);
        var creationMethodsArr = new JsonArray();
        foreach (var m in creationDocType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!m.Name.Contains("Roof", StringComparison.OrdinalIgnoreCase)) continue;
            var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            var obsolete = m.GetCustomAttribute<ObsoleteAttribute>();
            creationMethodsArr.Add(new JsonObject
            {
                ["signature"] = $"{m.Name}({ps}) : {m.ReturnType.Name}",
                ["obsolete"] = obsolete is not null,
                ["obsoleteMessage"] = obsolete?.Message,
            });
        }
        result["documentCreateRoofMethods"] = creationMethodsArr;

        // Same sweep on Autodesk.Revit.Creation.FamilyItemFactory (doc.FamilyCreate), in
        // case an in-place-family / extrusion route is viable instead.
        var famCreateType = typeof(Autodesk.Revit.Creation.FamilyItemFactory);
        var famMethodsArr = new JsonArray();
        foreach (var m in famCreateType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!m.Name.Contains("Extrusion", StringComparison.OrdinalIgnoreCase)
                && !m.Name.Contains("Roof", StringComparison.OrdinalIgnoreCase)) continue;
            var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            famMethodsArr.Add($"{m.Name}({ps}) : {m.ReturnType.Name}");
        }
        result["familyCreateExtrusionMethods"] = famMethodsArr;

        result["revitApiAssemblyVersion"] = typeof(Document).Assembly.GetName().Version?.ToString();

        return result;
    }
}
