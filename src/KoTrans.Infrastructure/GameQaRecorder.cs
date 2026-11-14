using System.Collections.Generic;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

// One immutable frame reference, no disk/encoding work on the OCR path. Snapshot is fixed at key press.
public sealed class GameQaRecorder
{
    private readonly object sync = new();
    private GameQaSnapshot? latest;
    public GameQaSnapshot? Snapshot { get { lock (sync) return latest; } }
    public void Clear() { lock (sync) latest = null; }
    public Guid Begin(ImageFrame input, OcrOptions options, GameQaProfile profile, double? captureMs = null,
        int? sourceWidth = null, int? sourceHeight = null, PixelRect? crop = null)
    {
        var id = Guid.NewGuid();
        lock (sync) latest = new(input, new(1, id, DateTimeOffset.UtcNow, null, "pending", options, profile,
            new(input.Width, input.Height, input.PixelFormat, sourceWidth ?? input.Width, sourceHeight ?? input.Height, crop),
            null, null, new("not_requested"), captureMs, AlgorithmVersion: OcrAlgorithmRevision.Current));
        return id;
    }
    public void Complete(Guid id, OcrResult result, string pipelineText, bool translate, OcrOptions? actualOptions = null, IReadOnlyList<OcrLine>? nvlPrefix = null)
    {
        lock (sync) if (latest?.Record.RequestId == id)
            latest = latest with { Record = latest.Record with { OcrStatus = "completed", CompletedUtc = DateTimeOffset.UtcNow,
                Options = actualOptions ?? latest.Record.Options, Ocr = result with { Lines = result.Lines.ToArray(), Candidates = result.Candidates?.ToArray(), Characters = result.Characters.ToArray(), DetectedBoxes = result.DetectedBoxes.ToArray() },
                NvlPrefix = nvlPrefix?.ToArray() ?? [],
                PipelineText = pipelineText, Translation = new(translate && pipelineText.Length > 0 ? "pending" : "not_requested") } };
    }
    public void Finish(Guid id, string status)
    {
        if (status is not ("canceled" or "failed")) throw new ArgumentException("Invalid terminal status.");
        lock (sync) if (latest?.Record.RequestId == id)
        {
            var record = latest.Record;
            latest = latest with { Record = record.OcrStatus == "pending" ? record with { OcrStatus = status }
                : record.Translation.Status == "pending" ? record with { Translation = new(status) } : record };
        }
    }
    public void Translated(Guid id, string text, double apiMs)
    {
        lock (sync) if (latest?.Record.RequestId == id && latest.Record.OcrStatus == "completed" && latest.Record.Translation.Status == "pending")
            latest = latest with { Record = latest.Record with { Translation = new("completed", text, apiMs) } };
    }
}
