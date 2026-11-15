using KoTrans.Core;
using KoTrans.Ocr;
using OpenCvSharp;

internal static class DialoguePrefixRecoveryTests
{
    internal static void Run()
    {
        int checks = 0, calls = 0;
        void Check(bool valid, string message) { if (!valid) throw new Exception(message); checks++; }
        var options = new OcrOptions("ja", "ADV", false);
        var area = new PixelRect(0, 0, 600, 120);
        using var image = new Mat(120, 600, MatType.CV_8UC3, new Scalar(25, 50, 70));
        foreach (int x in new[] {60, 85, 260, 300, 340}) Cv2.Rectangle(image, new Rect(x, 28, 12, 22), Scalar.White, -1);
        foreach (int x in new[] {110, 140, 170, 200, 230}) Cv2.Rectangle(image, new Rect(x, 38, 22, 4), Scalar.White, -1);
        using var original = image.Clone();
        var suffix = new OcrLine("字!!」", .96f, new(250, 20, 180, 40));
        const string complete = "「あーーーーい!!」";
        var recovered = DialoguePrefixRecovery.Recover(image, [suffix], options, crop => {
            calls++; Check(crop.Width == 378 && crop.Height == 40, "Prefix crop did not follow connected glyphs");
            return (complete, .95f);
        });
        Check(calls == 1 && recovered.Single().Text == complete && recovered[0].Bounds.X == 52,
            "Visible prefix was lost before a confidently recognized suffix");
        Check(Cv2.Norm(image, original) == 0, "OCR preprocessing changed source pixels");
        foreach (var mode in new[] {options with {Language="en"}, options with {ReadingMode="NVL"}, options with {Vertical=true}})
            Check(DialoguePrefixRecovery.Recover(image, [suffix], mode, _ => throw new Exception("Unexpected recognition"))[0] == suffix,
                "Unrelated reading mode changed");
        Check(DialoguePrefixRecovery.Recover(image, [suffix], options, _ => (complete, .89f))[0] == suffix,
            "Weak retry replaced a suffix");
        Check(DialoguePrefixRecovery.Recover(image, [suffix], options, _ => ("「全く別の文字です。」", .99f))[0] == suffix,
            "Retry with a different ending replaced evidence");
        Check(DialoguePrefixRecovery.Recover(image, [suffix], options, _ => ("ABCDEF!!」", .99f))[0] == suffix,
            "Latin hallucination replaced Japanese text");
        Check(DialoguePrefixRecovery.FindPrefix(image, suffix.Bounds, new(200,0,400,120), []) is null,
            "Prefix recovery escaped the requested search area");
        Check(DialoguePrefixRecovery.FindPrefix(image, suffix.Bounds, area, [new(60,20,150,40)]) is null,
            "An independently detected preceding line was swallowed");
        using var gap = image.Clone(); Cv2.Rectangle(gap, new(165,20,35,40), Scalar.Black, -1);
        Check(DialoguePrefixRecovery.FindPrefix(gap, suffix.Bounds, area, []) is null,
            "A distant label was joined across an empty gap");
        using var clipped = image.Clone(); Cv2.Rectangle(clipped, new(50,20,200,40), Scalar.White, -1);
        Check(DialoguePrefixRecovery.FindPrefix(clipped, suffix.Bounds, area, []) is null,
            "Clipped scenery established a missing prefix");
        using var coloured = new Mat(120,600,MatType.CV_8UC3,Scalar.Black);
        foreach (int x in new[] {60,85,110,140,170,200,230,260,300,340})
            Cv2.Rectangle(coloured, new(x,28,20,22), new Scalar(40,80,230), -1);
        Check(DialoguePrefixRecovery.FindPrefix(coloured, suffix.Bounds, area, []) is null,
            "Coloured artwork was treated as light dialogue glyphs");
        Console.WriteLine($"Dialogue prefix recovery: {checks} assertions passed (synthetic pixels/Mock).");
    }
}
