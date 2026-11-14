using System.Globalization;
using System.Text;
using System.Reflection;
using System.Collections.Generic;
namespace KoTrans.Infrastructure;

public static class DebugReportWriter
{
    public static string Format(DebugSnapshot snapshot, string version, string commit)
    {
        var b = new StringBuilder("KO TRANS 2 - DEBUG REPORT\n[APPLICATION]\n");
        b.AppendLine($"Timestamp: {snapshot.Time:O}\nBuild Version: {DebugTelemetry.Label(version)}\nGit Commit: {(commit.Length == 40 && commit.All(Uri.IsHexDigit) ? commit : "N/A")}\nDebug Enabled: True\nSession: {snapshot.Session}\nSession Duration: {snapshot.Duration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s");
        b.AppendLine("[CAPTURE / FRAME GATE / OCR ENGINE / TRANSLATION]");
        foreach (var field in typeof(DebugValues).GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            object? value = field.GetValue(snapshot.Values);
            b.AppendLine(field.Name + ": " + Value(value));
        }
        b.AppendLine("Marker counters: cumulative for the current translation session. ExcludedTotal counts removed indicators; ExcludingOcrCalls counts affected service calls; SkippedFrames counts learned-marker-only frame decisions, not a measured A/B reduction. ExtraBodyChecks counts additional recognizer verification passes separately from LogicalOcrCalls. BodyCacheHits counts exact-crop verification results reused without additional recognition.");
        b.AppendLine("OcrResultCacheHits: exact pixel + OCR option matches reused in this session; not included in LogicalOcrCalls or additional body inference. Current learning/removal still runs. Cache is RAM-only and bounded to 32 requests.");
        b.AppendLine("RecognitionInferences / RecognitionCacheHits: cumulative actual recognizer executions / exact prepared-input reuses from completed OCR requests; distinct from whole-frame LogicalOcrCalls. Shared by ordinary lines and marker body checks. Existing crop-cache hits can skip this layer. Failed/cancelled OCR requests may have unfinished work not included in these totals.");
        b.AppendLine("Cache miss reasons: Empty=no stored result; InputNotCached=no matching retained input; ConditionsChanged=same retained input, different OCR/model conditions; Evicted=exact key found in bounded recent eviction history; Bypassed=explicit fresh recognition. Eviction history retains 4x cache capacity; older misses are InputNotCached. No key/image/text is recorded. ExtraBodyChecks includes continuation-tail and learned-marker verification; common-cache hits are not actual extra body inference.");
        b.AppendLine("LastAiResult*: latest successful, still-current provider response before NVL prefix composition. Equality uses private RAM-only hashes after normalizing CRLF and trimming outer whitespace; source/translation/hash values are not saved. AiRepeatedResultForChangedSource counts identical responses to different source strings, not confirmed translation errors. Cache replay, cancelled and stale responses do not count.");
        b.AppendLine("Overlay*MatchesUpdate compares actual WPF text controls with fields supplied by that update. N/A means that field was not supplied, including source-only/partial updates; it is not a mismatch. Counts/lengths do not measure translation accuracy or prove that the captured source matches the game. OverlayContentMismatches counts updates with a differing supplied field, including hidden controls. No subtitle text is retained.");
        b.AppendLine("SourceVerification*: one delayed full OCR after an ordinary continuous NVL acceptance, bypassing OCR caches and prior row stabilization. Comparison is against the accepted canonical source, not translation accuracy. A mismatch may also mean the player advanced. Unchanged source does not request AI again. Source text and images are not retained.");
        b.AppendLine("[OCR CYCLE]"); b.AppendLine(Cycle(snapshot.Current));
        b.AppendLine("Previous: " + Cycle(snapshot.Previous));
        b.AppendLine("[RECENT CYCLES]"); foreach (var cycle in snapshot.Cycles) b.AppendLine(Cycle(cycle));
        b.AppendLine("[RECENT EVENTS] (newest first; values belong to event time)");
        foreach (var entry in snapshot.Events) {
            b.AppendLine($"{entry.Time:O} session={entry.Session} {entry.Code} reason={entry.Reason} {Cycle(entry.Cycle)}");
            foreach (var field in typeof(DebugValues).GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                object? value = field.GetValue(entry.Values);
                b.AppendLine("  " + field.Name + ": " + Value(value));
            }
        }
        b.AppendLine("[LAST ERROR]\nCategory / Reason: " + snapshot.Values.LastError);
        b.AppendLine("Privacy: no source/translation text, images, titles, paths, profile names, keys or raw exceptions. N/A means not measured. DetectedRegions is detector output; AcceptedLines is OCR filtered lines, not dialogue acceptance.");
        return b.ToString();
    }
    private static string Value(object? value) => value switch {
        KoTrans.Core.OcrCacheMissCounts misses => System.Text.Json.JsonSerializer.Serialize(misses),
        IReadOnlyList<KoTrans.Core.OcrMarkerObservation> observations => System.Text.Json.JsonSerializer.Serialize(observations,
            new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }),
        string text => DebugTelemetry.Label(text),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "N/A" };
    private static string Cycle(DebugCycle? c) => c is null ? "N/A" : $"Session={c.Session} Cycle=#{c.Id} Started={c.Started:O} Result={c.Status} OCR={c.OcrCalls} OcrCache={c.OcrCacheHits} AI={c.AiRequests} FrameReuse={c.Reuses} FullRetry={c.FullRetries} TranslationCache={c.CacheHits} Recognizer={c.RecognitionInferences} RecognitionCache={c.RecognitionCacheHits} OcrCacheMisses={Value(c.OcrCacheMisses)} RecognitionCacheMisses={Value(c.RecognitionCacheMisses)}";
    public static async Task<string> SaveAsync(string directory, DebugSnapshot snapshot, string version, string commit)
    {
        string content = Format(snapshot, version, commit);
        string folder = Path.Combine(directory, "DebugReports"); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "Debug_" + snapshot.Time.ToLocalTime().ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, true);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content)).ConfigureAwait(false); return path;
    }
}
