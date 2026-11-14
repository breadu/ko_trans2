using KoTrans.Core;
using KoTrans.Ocr;
using OpenCvSharp;

internal static class OverlappingRowRecoveryTests
{
    internal static void Run()
    {
        int count = 0;
        void Check(bool valid, string message) { if (!valid) throw new Exception(message); count++; }
        var options = new OcrOptions("ja", "ADV", false);
        using var image = new Mat(150, 400, MatType.CV_8UC3, new Scalar(30, 60, 80));
        foreach (int x in new[] {26, 52, 120, 150, 170})
            Cv2.Rectangle(image, new Rect(x, 22, 10, 20), Scalar.White, -1);
        // A broad clipped background highlight must not establish the row.
        Cv2.Rectangle(image, new Rect(40, 0, 40, 12), Scalar.White, -1);
        OcrLine first = new("雲", .89f, new(20, 0, 55, 46));
        OcrLine rest = new("木ー字", .79f, new(43, 0, 141, 57));
        OcrLine unrelated = new("次の行です。", .99f, new(20, 90, 180, 30));
        var original = new List<OcrLine> {first, rest, unrelated};
        int calls = 0;
        (string, float) Read(PixelRect crop) {
            calls++;
            Check(crop == new PixelRect(20, 18, 164, 28), "Retry did not follow the observed glyph row");
            return ("「あいうえ！」", .98f);
        }
        var result = OverlappingRowRecovery.Recover(image, original, options, Read);
        Check(calls == 1 && result.Count == 2 && result.Any(l => l.Text == "「あいうえ！」"),
            "Overlapping uncertain fragments were concatenated instead of rereading the row");
        Check(result.Contains(unrelated) && original.SequenceEqual(new[] {first, rest, unrelated}),
            "Recovery changed another row or mutated input evidence");
        Check(OverlappingRowRecovery.Recover(image, original, options, _ => ("「あいうえ！」", .88f)).SequenceEqual(original),
            "Weak retry replaced the initial result");
        Check(OverlappingRowRecovery.Recover(image, original, options, _ => ("ABCD", .99f)).SequenceEqual(original),
            "Latin retry replaced Japanese dialogue");
        calls = 0;
        foreach (var mode in new[] {options with {Language="en"}, options with {ReadingMode="NVL"}, options with {Vertical=true}})
            Check(OverlappingRowRecovery.Recover(image, original, mode, Read).SequenceEqual(original), "Unrelated mode changed");
        Check(calls == 0, "Unrelated mode ran recognition");
        Check(OverlappingRowRecovery.Recover(image, [first with {Confidence=.98f}, rest with {Confidence=.98f}], options, Read).Count == 2
            && calls == 0, "Confident fragments were retried");
        Check(OverlappingRowRecovery.Recover(image, [first, unrelated], options, Read).Count == 2
            && calls == 0, "Separate rows were combined");
        using var blank = new Mat(150, 400, MatType.CV_8UC3, Scalar.Black);
        Check(OverlappingRowRecovery.Recover(blank, original, options, Read).SequenceEqual(original) && calls == 0,
            "Missing visual evidence invented a text row");
        using var twoRows = new Mat(150, 400, MatType.CV_8UC3, Scalar.Black);
        foreach (int y in new[] {20, 70}) foreach (int x in new[] {25, 85, 145, 205, 275})
            Cv2.Rectangle(twoRows, new Rect(x, y, 10, 18), Scalar.White, -1);
        Check(OverlappingRowRecovery.FindRow(twoRows, new(20, 0, 300, 110)) is null,
            "Two visible rows were reduced to a single-row crop");
        Cv2.Rectangle(twoRows, new Rect(20, 65, 300, 40), Scalar.Black, -1);
        Cv2.Rectangle(twoRows, new Rect(25, 70, 10, 18), Scalar.White, -1);
        Check(OverlappingRowRecovery.FindRow(twoRows, new(20, 0, 300, 110)) is null,
            "A one-character second row was cropped away");
        Check(OverlappingRowRecovery.FindRow(image, new(-1, 0, 164, 57)) is null,
            "Invalid crop bounds were accepted");
        Console.WriteLine($"Overlapping row recovery: {count} assertions passed (synthetic pixels and Mock recognition).");
    }
}
