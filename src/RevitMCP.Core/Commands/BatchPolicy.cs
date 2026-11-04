using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Pure batch-validation helpers that do not reference any Revit API types,
/// making them directly unit-testable without a Revit install.
/// </summary>
public static class BatchPolicy
{
    /// <summary>
    /// Returns a bad_request error envelope if the given kinds mix ModelWrite
    /// and UiAction; otherwise null.
    /// </summary>
    public static JsonObject? ValidateBatchKinds(IEnumerable<ExecutionKind> kinds)
    {
        bool anyWrite = false, anyUi = false, anySelfManaged = false;
        foreach (var k in kinds)
        {
            if (k == ExecutionKind.ModelWrite) anyWrite = true;
            if (k == ExecutionKind.UiAction)   anyUi   = true;
            if (k == ExecutionKind.SelfManagedTransaction) anySelfManaged = true;
        }
        if (anySelfManaged)
            return JsonResult.Error("bad_request",
                "This command manages its own transaction (e.g. create_stair) and " +
                "cannot run inside a batch. Call it on its own.");
        if (anyWrite && anyUi)
            return JsonResult.Error("bad_request",
                "Batch mixes ModelWrite and UiAction commands. " +
                "Submit model writes and UI actions as separate batches.");
        return null;
    }
}
