using System.Text.Json;
using KoTrans.Core;
using KoTrans.Ocr;

internal static class OcrExecutionPolicyTests
{
    internal static async Task Run()
    {
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() => {
            uint before = OcrExecutionPolicy.DenormalControl;
            using (OcrExecutionPolicy.Enter()) {
                if (OcrExecutionPolicy.DenormalControl != OcrExecutionPolicy.FlushDenormals)
                    throw new Exception("OCR worker did not enable both FTZ and DAZ");
                using (OcrExecutionPolicy.Enter()) { }
                if (OcrExecutionPolicy.DenormalControl != OcrExecutionPolicy.FlushDenormals)
                    throw new Exception("Nested OCR scope restored the wrong state");
            }
            if (OcrExecutionPolicy.DenormalControl != before)
                throw new Exception("OCR leaked floating-point state into its worker");
            try { OcrExecutionPolicy.Run((Action)(() => throw new OperationCanceledException())); }
            catch (OperationCanceledException) { }
            if (OcrExecutionPolicy.DenormalControl != before)
                throw new Exception("Canceled/failed OCR leaked floating-point state");
            try { OcrExecutionPolicy.Run((Action)(() => OcrExecutionPolicy.Run((Action)(() => throw new IOException("synthetic failure"))))); }
            catch (IOException) { }
            if (OcrExecutionPolicy.DenormalControl != before)
                throw new Exception("Nested failed OCR leaked floating-point state");
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await Task.WhenAll(workers);
        var model = new OcrModelIdentity("test", "test", new string('A', 64), "", "");
        var old = new OcrRunIdentity(model, model, true, 2);
        if (JsonSerializer.Deserialize<OcrRunIdentity>(JsonSerializer.Serialize(old))!.FlushDenormalsToZero)
            throw new Exception("Legacy QA silently acquired the new numeric policy");
        var current = old with { FlushDenormalsToZero = true };
        if (!JsonSerializer.Deserialize<OcrRunIdentity>(JsonSerializer.Serialize(current))!.FlushDenormalsToZero)
            throw new Exception("New QA lost the numeric policy");
        foreach (var saved in new[] { old, current with { ImplementationVersion = "ocr-layout-v6-20261031" } }) {
            try {
                OcrServiceFactory.PreparePaddleReplay("unused", new("ja", "NVL", false), saved);
                throw new Exception("Old policy/implementation was replayed as current");
            }
            catch (InvalidDataException) { }
        }
        Console.WriteLine("PASS: OCR numeric policy on four independent workers, nested/exception restoration, and QA policy identity/rejection.");
    }
}
