using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a Stairs element with real treads/risers between two levels
/// (Autodesk.Revit.DB.Architecture.Stairs via StairsEditScope) — NOT a floor
/// opening and NOT a ramp. A single straight run by default; with "runs" it
/// builds several straight runs in ONE Stairs element and joins consecutive
/// runs with real stairs landings (StairsLanding), like sketching a
/// multi-flight stair interactively. Riser height comes from the level height
/// and the stairs type; each run's riser count from its walking-line length
/// (rounded to a multiple of the tread depth).
///
/// Parameters (units: meters by default; pass "units":"feet" for imperial):
///   - start:           { x, y }         walking-line start, at the base level (single run)
///   - end:             { x, y }         walking-line end (single run)
///   - runs:            [ { start:{x,y}, end:{x,y} }, ... ]  optional — several runs; replaces
///                      start/end. Run 1 starts at the base level, each next run at the top of the
///                      previous one.
///   - landingProfiles: [ [ {x,y}, ... ], ... ]  optional — closed outline of the landing after run i
///                      (sketched landing at that run's top). Missing/null entries get an automatic
///                      landing (StairsLanding.CreateAutomaticLanding).
///   - treadDepth:      number           optional — Stairs.ActualTreadDepth, set before the runs
///   - runWidth:        number           optional — StairsRun.ActualRunWidth for every run
///   - desiredRisers:   int              optional — Stairs.DesiredRisersNumber
///   - baseLevelName:   string           optional, defaults to lowest level
///   - topLevelName:    string           optional, defaults to the next level above base
///   - stairsTypeName:  string           optional, defaults to the document's current default
///   - units:           "meters"|"feet"  optional, default "meters"
/// </summary>
public sealed class CreateStairCommand : IRevitCommand
{
    public string Name => "create_stair";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    // StairsEditScope refuses to start while a transaction is already open
    // ("the document is currently modifiable") — it must own the whole
    // transaction itself, so the dispatcher must not wrap one. See
    // ExecutionKind.SelfManagedTransaction.
    public ExecutionKind Execution => ExecutionKind.SelfManagedTransaction;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);
        var scale = units == "feet" ? 1.0 : P.MetersToFeet;

        var paths = ReadRunPaths(p, units);
        var multiRun = paths.Count > 1;
        var landingLoops = ReadLandingProfiles(p, units, paths.Count);

        double? treadDepth = p["treadDepth"] is null ? null : P.Dbl(p, "treadDepth") * scale;
        double? runWidth = p["runWidth"] is null ? null : P.Dbl(p, "runWidth") * scale;
        int? desiredRisers = p["desiredRisers"] is null ? null : P.Int(p, "desiredRisers");

        var baseLevel = CreateWallCommand.ResolveLevel(doc, P.StrOrNull(p, "baseLevelName"));
        var topLevel = ResolveTopLevel(doc, baseLevel, P.StrOrNull(p, "topLevelName"));
        if (topLevel.Elevation <= baseLevel.Elevation)
            throw new RevitCommandException("invalid_parameter",
                $"Top level '{topLevel.Name}' must be above base level '{baseLevel.Name}'.");

        var stairsType = ResolveStairsType(doc, P.StrOrNull(p, "stairsTypeName"));

        // StairsEditScope refuses to start while ANY Transaction is open — it
        // manages its own transaction internally. A TransactionGroup is not
        // itself a modifiable transaction (it only assimilates/rolls back
        // already-committed transactions inside it), so it does not trigger
        // that restriction and still gives us one dry-run-able unit.
        ElementId stairsId;
        string? resultStairsTypeName;
        var runInfo = new JsonArray();
        var landingIds = new JsonArray();
        var warnings = new JsonArray();
        using (var group = new TransactionGroup(doc, "MCP: create_stair"))
        {
            group.Start();

            // Multi-run: the type must be in place before the runs exist, or the
            // later type swap re-derives riser height/tread and breaks the landings.
            var typeAppliedInScope = false;
            using (var scope = new StairsEditScope(doc, "Create Stairs"))
            {
                stairsId = scope.Start(baseLevel.Id, topLevel.Id);

                using (var sketchTx = new Transaction(doc, "Sketch Stair Run"))
                {
                    sketchTx.Start();
                    var stairs = (Stairs)doc.GetElement(stairsId);
                    if (multiRun && stairsType is not null)
                    {
                        stairs.ChangeTypeId(stairsType.Id);
                        typeAppliedInScope = true;
                    }
                    if (desiredRisers is int n) stairs.DesiredRisersNumber = n;
                    if (treadDepth is double t) stairs.ActualTreadDepth = t;

                    var runs = new List<StairsRun>();
                    var z = baseLevel.Elevation;
                    foreach (var (start, end) in paths)
                    {
                        var runLine = Line.CreateBound(new XYZ(start.X, start.Y, z), new XYZ(end.X, end.Y, z));
                        var run = StairsRun.CreateStraightRun(doc, stairsId, runLine, StairsRunJustification.Center);
                        if (runWidth is double w) run.ActualRunWidth = w;
                        runs.Add(run);
                        // TopElevation is relative to the stairs base; the next run's
                        // location line Z is in model coordinates.
                        z = baseLevel.Elevation + run.TopElevation;
                    }

                    for (var i = 0; i < runs.Count - 1; i++)
                    {
                        if (landingLoops[i] is { } loopPts)
                        {
                            var elev = runs[i].TopElevation;
                            var loop = new CurveLoop();
                            for (var k = 0; k < loopPts.Count; k++)
                            {
                                var a = loopPts[k];
                                var b = loopPts[(k + 1) % loopPts.Count];
                                loop.Append(Line.CreateBound(
                                    new XYZ(a.X, a.Y, baseLevel.Elevation + elev),
                                    new XYZ(b.X, b.Y, baseLevel.Elevation + elev)));
                            }
                            landingIds.Add(StairsLanding.CreateSketchedLanding(doc, stairsId, loop, elev).Id.Value);
                        }
                        else if (StairsLanding.CanCreateAutomaticLanding(doc, runs[i].Id, runs[i + 1].Id))
                        {
                            foreach (var id in StairsLanding.CreateAutomaticLanding(doc, runs[i].Id, runs[i + 1].Id))
                                landingIds.Add(id.Value);
                        }
                        else
                        {
                            warnings.Add($"No automatic landing possible between run {i + 1} and run {i + 2} " +
                                         "(runs not aligned/at the same height) — pass landingProfiles[" + i + "].");
                        }
                    }

                    foreach (var run in runs)
                    {
                        runInfo.Add(new JsonObject
                        {
                            ["id"] = run.Id.Value,
                            ["baseElevation"] = run.BaseElevation / scale,
                            ["topElevation"] = run.TopElevation / scale,
                            ["actualRunWidth"] = run.ActualRunWidth / scale,
                            ["risers"] = run.ActualRisersNumber,
                        });
                    }
                    sketchTx.Commit();
                }

                scope.Commit(new AcceptAllFailuresPreprocessor());
            }

            if (stairsType is not null && !typeAppliedInScope)
            {
                using var tx = new Transaction(doc, "MCP: create_stair (type)");
                tx.Start();
                doc.GetElement(stairsId)?.ChangeTypeId(stairsType.Id);
                tx.Commit();
            }
            resultStairsTypeName = doc.GetElement(stairsId)?.Name;

            if (ctx.DryRun) group.RollBack();
            else group.Assimilate();
        }

        var result = new JsonObject
        {
            ["id"] = stairsId.Value,
            ["baseLevelName"] = baseLevel.Name,
            ["topLevelName"] = topLevel.Name,
            ["stairsTypeName"] = resultStairsTypeName,
        };
        if (multiRun)
        {
            result["runs"] = runInfo;
            result["landingIds"] = landingIds;
            if (warnings.Count > 0) result["warnings"] = warnings;
        }
        return result;
    }

    /// <summary>Single run from start/end, or several from "runs".</summary>
    private static List<(XYZ Start, XYZ End)> ReadRunPaths(JsonObject p, string units)
    {
        var paths = new List<(XYZ, XYZ)>();
        if (p["runs"] is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not JsonObject r)
                    throw new RevitCommandException("bad_request", $"runs[{i}] must be an object with start and end.");
                paths.Add((P.Xyz(r, "start", units), P.Xyz(r, "end", units)));
            }
            if (paths.Count == 0)
                throw new RevitCommandException("bad_request", "'runs' must contain at least one run.");
        }
        else
        {
            paths.Add((P.Xyz(p, "start", units), P.Xyz(p, "end", units)));
        }
        foreach (var (s, e) in paths)
        {
            if (new XYZ(s.X, s.Y, 0).DistanceTo(new XYZ(e.X, e.Y, 0)) < 1e-6)
                throw new RevitCommandException("invalid_parameter", "Start and end points are coincident.");
        }
        return paths;
    }

    /// <summary>One optional landing outline per gap between runs (null = automatic).</summary>
    private static List<IList<XYZ>?> ReadLandingProfiles(JsonObject p, string units, int runCount)
    {
        var loops = Enumerable.Repeat<IList<XYZ>?>(null, System.Math.Max(0, runCount - 1)).ToList();
        if (p["landingProfiles"] is not JsonArray arr) return loops;
        if (arr.Count > loops.Count)
            throw new RevitCommandException("bad_request",
                $"landingProfiles has {arr.Count} entries but there are only {loops.Count} gaps between runs.");
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is null) continue;
            var pts = P.XyzList(new JsonObject { ["pts"] = arr[i]!.DeepClone() }, "pts", units);
            if (pts.Count < 3)
                throw new RevitCommandException("bad_request", $"landingProfiles[{i}] needs at least 3 points.");
            loops[i] = pts;
        }
        return loops;
    }

    private static Level ResolveTopLevel(Document doc, Level baseLevel, string? name)
    {
        var query = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
        if (!string.IsNullOrWhiteSpace(name))
        {
            return query.FirstOrDefault(l => l.Name == name)
                ?? throw new RevitCommandException("not_found", $"Level '{name}' not found.");
        }
        return query.Where(l => l.Elevation > baseLevel.Elevation)
                    .OrderBy(l => l.Elevation)
                    .FirstOrDefault()
            ?? throw new RevitCommandException("not_found",
                "No level above the base level exists — create one first with create_level.");
    }

    private static StairsType? ResolveStairsType(Document doc, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var query = new FilteredElementCollector(doc).OfClass(typeof(StairsType)).Cast<StairsType>();
        return query.FirstOrDefault(t => t.Name == name)
            ?? throw new RevitCommandException("not_found", $"StairsType '{name}' not found.");
    }

    /// <summary>Accepts default resolutions for ordinary stair-sketch warnings
    /// (e.g. "risers not equal") rather than leaving them to block the
    /// commit; real blocking errors still surface as an exception.</summary>
    private sealed class AcceptAllFailuresPreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (var failure in accessor.GetFailureMessages())
                accessor.DeleteWarning(failure);
            return FailureProcessingResult.Continue;
        }
    }
}
