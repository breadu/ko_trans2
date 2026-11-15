using KoTrans.Core;
using KoTrans.Ocr;

internal static class DialogueRegionSelectionTests
{
    internal static void Run()
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        var options = new OcrOptions("ja", "ADV", false, ModelPresetId: "v6-medium-full");
        Check(DialogueRegionSelection.Supported(options), "Current horizontal Japanese ADV route missing");
        Check(!JapaneseAdvRecovery.Supports(options), "Old state-based label retry re-entered the region route");
        foreach (var other in new[] { options with { Language = "en" }, options with { ReadingMode = "NVL" },
            options with { Vertical = true }, options with { ModelPresetId = "default" },
            options with { SuppressFurigana = false }, options with { KeepLocalParagraph = true } })
            Check(!DialogueRegionSelection.Supported(other), "Unsupported layout changed");
        PixelRect[] body = [new(40, 30, 500, 30), new(50, 72, 100, 28)];
        PixelRect[] input = [new(250, 5, 60, 14), body[1], new(420, 115, 80, 15), body[0]];
        var selected = DialogueRegionSelection.Select(input, b => b);
        Check(selected.SequenceEqual(body), "Separate labels entered the body or wrapped last row was lost");
        Check(selected.All(input.Contains), "Selection changed original detector margins");
        Check(DialogueRegionSelection.WithEndMargin(new(20, 10, 100, 32), [], 200) == new PixelRect(16, 10, 108, 32), "Small symmetric end margin or vertical band changed");
        Check(DialogueRegionSelection.WithEndMargin(new(0, 10, 198, 32), [], 200) == new PixelRect(0, 10, 200, 32), "End margin escaped the frame");
        Check(DialogueRegionSelection.WithEndMargin(new(20, 10, 100, 32), [new(122, 10, 40, 32)], 200).Width == 105, "End margin crossed a neighbouring glyph crop");
        Check(DialogueRegionSelection.Select(input.Reverse().ToArray(), b => b).SequenceEqual(body), "Detector enumeration changed selection");
        // A nearby but vertically displaced label must not redefine the start
        // of a text row and thereby discard a wrapped continuation underneath.
        foreach (int shift in new[] { -12, 12 }) {
            PixelRect[] wrapped = [new(120, 50, 600, 40), new(130, 110, 200, 38), new(130, 168, 26, 36)];
            var labelled = wrapped.Append(new PixelRect(38, 50 + shift, 60, 36)).ToArray();
            Check(DialogueRegionSelection.Select(labelled, b => b).SequenceEqual(wrapped), "Displaced side label dropped a wrapped row");
            Check(DialogueRegionSelection.Select(labelled.Reverse().ToArray(), b => b).SequenceEqual(wrapped), "Side label selection depends on detector order");
        }
        PixelRect[] jittered = [new(120, 50, 200, 40), new(335, 55, 200, 38), new(130, 110, 80, 38)];
        Check(DialogueRegionSelection.Select(jittered, b => b).Count == 3, "Small same-baseline detector jitter split real text");
        PixelRect[] shortFirst = [new(40, 25, 40, 30), new(45, 65, 400, 30)];
        Check(DialogueRegionSelection.Select(shortFirst, b => b).SequenceEqual(shortFirst), "Short first row lost when second row is widest");
        PixelRect[] fragments = [new(170, 30, 75, 28), new(40, 32, 65, 28), new(110, 31, 55, 28), new(300, 100, 160, 28)];
        Check(DialogueRegionSelection.Select(fragments, b => b).Count == 3, "Same-row bridge did not connect all fragments");
        var one = new PixelRect(150, 30, 12, 28);
        Check(DialogueRegionSelection.Select(new[] { one }, b => b).Single() == one, "One-character dialogue discarded");
        Check(DialogueRegionSelection.Select(Array.Empty<PixelRect>(), b => b).Count == 0, "Empty capture acquired invented region");
        var lines = DialogueRegionSelection.Arrange([new("C7", .98f, new(40, 30, 60, 28)),
            new("ABC", .99f, new(105, 30, 65, 28)), new("！", .97f, new(42, 70, 16, 28))]);
        Check(lines.Count == 2 && lines[0].Text == "C7ABC" && lines[1].Text == "！", "Valid Latin/digits/short text were filtered");
        var frame = new ImageFrame(600, 150, 2400, new byte[360000]);
        var crop = new PixelRect(20, 10, 500, 120);
        var restored = OcrRegionTracker.Restore(new(lines, TimeSpan.Zero) { Candidates = lines }, crop, frame, options);
        Check(restored.Text == "C7ABC\n！" && restored.Lines[0].Bounds.X == 60 && restored.Lines[0].Bounds.Y == 40,
            "ROI restoration changed selected text or coordinates");
        Console.WriteLine($"Dialogue region selection: {count} passed");
    }
}
