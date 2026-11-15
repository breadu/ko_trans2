using KoTrans.Core;
using KoTrans.Ocr;

internal static class DialogueRegionSelectionTests
{
    internal static void Run()
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        var options = new OcrOptions("ja", "ADV", false, ModelPresetId: "v6-medium-full");
        string[] presets = ["default", "v6-medium-full", "v5-det-v6-medium-rec"];
        foreach (string preset in presets) {
            var supported = options with { ModelPresetId = preset };
            Check(DialogueRegionSelection.Supported(supported), "Horizontal Japanese ADV route missing: " + preset);
            Check(DialogueRegionSelection.Supported(supported with { UseDirectMl = true }), "GPU worker uses a different region policy: " + preset);
            Check(!JapaneseAdvRecovery.Supports(supported), "Old state-based label retry re-entered the region route: " + preset);
        }
        foreach (var other in new[] { options with { Language = "en" }, options with { ReadingMode = "NVL" },
            options with { Vertical = true }, options with { ModelPresetId = "unknown" }, options with { EngineId = "other" },
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
        PixelRect[] spaced = [new(100, 50, 180, 40), new(312, 50, 240, 40), new(105, 104, 30, 40)];
        Check(DialogueRegionSelection.Select(spaced, b => b).SequenceEqual(spaced), "A normal word space discarded the beginning of a sentence");
        foreach (int scale in new[] { 1, 2, 3 }) {
            PixelRect Scale(PixelRect b) => new(b.X * scale, b.Y * scale, b.Width * scale, b.Height * scale);
            PixelRect[] text = [Scale(new(100, 60, 70, 40)), Scale(new(174, 62, 310, 40)), Scale(new(104, 115, 24, 38))];
            PixelRect[] clutter = [.. text, Scale(new(20, 170, 600, 130)), Scale(new(30, 70, 40, 30))];
            Check(DialogueRegionSelection.Select(clutter, b => b).SequenceEqual(text), "Large patch or detached prefix displaced dialogue");
            Check(DialogueRegionSelection.Select(clutter.Reverse().ToArray(), b => b).SequenceEqual(text), "Paragraph selection depends on detector order/scale");
            PixelRect[] separate = [Scale(new(100, 20, 500, 40)), Scale(new(106, 72, 210, 40))];
            var between = separate.Append(Scale(new(65, 50, 50, 28))).ToArray();
            Check(DialogueRegionSelection.Select(between, b => b).SequenceEqual(separate), "Overlapping side fragment became a third dialogue line");
            PixelRect[] wrapped = [Scale(new(100, 50, 600, 40)), Scale(new(110, 110, 150, 38))];
            var prefixed = wrapped.Append(Scale(new(20, 55, 60, 35))).ToArray();
            Check(DialogueRegionSelection.Select(prefixed, b => b).SequenceEqual(wrapped), "Aligned continuation lost to a distant leading fragment");
            Check(DialogueRegionSelection.Select(prefixed.Reverse().ToArray(), b => b).SequenceEqual(wrapped), "Shared paragraph start depends on detector order");
        }
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
        foreach (string preset in presets) {
            var restored = OcrRegionTracker.Restore(new(lines, TimeSpan.Zero) { Candidates = lines }, crop, frame, options with { ModelPresetId = preset });
            Check(restored.Text == "C7ABC\n！" && restored.Lines[0].Bounds.X == 60 && restored.Lines[0].Bounds.Y == 40,
                "ROI restoration changed selected text or coordinates: " + preset);
        }
        Console.WriteLine($"Dialogue region selection: {count} passed");
    }
}
