using KoTrans.Core;
using KoTrans.Ocr;

internal static class RubySuppressionTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        // Synthetic text with the geometry of an enlarged DB annotation. No game image/text.
        var reading = new OcrLine("かんじ", .97f, new(847, 1035, 57, 26));
        var body = new OcrLine("あいうえおかきく漢字けこ、", .99f, new(503, 1055, 508, 41));
        var next = new OcrLine("これは別の本文です。", .99f, new(586, 1120, 837, 44));
        var options = new OcrOptions("ja", "NVL", false);
        // A large false text box below dialogue is not a kanji/ruby parent.
        // Geometry is synthetic; the proportions reproduce the failure in a scaled capture.
        var dialogue = new OcrLine("明日は公園で友達と本を読みましょう。", .98f, new(47,57,586,47));
        var decoration = new OcrLine("abcdef", .58f, new(25,90,460,99));
        Check(LayoutProcessor.IsRuby(dialogue.Bounds, decoration.Bounds, false), "Fixture must expose the geometric false positive");
        var preserved = LayoutProcessor.Arrange([dialogue, decoration], options, 839, 189);
        Check(preserved.Any(l => l.Text.Contains(dialogue.Text)), "Full Japanese dialogue was deleted as ruby of a Latin decoration");
        var kanaSentence = dialogue with { Text = "かしこまりました。" };
        Check(LayoutProcessor.Arrange([kanaSentence, decoration], options, 839, 189).Any(l => l.Text.Contains(kanaSentence.Text)),
            "Kana sentence was deleted before a credible kanji parent was established");
        var label = new OcrLine("案内", .99f, new(66,8,62,37));
        var smallNoise = new OcrLine("ABCDE", .97f, new(512,166,24,10));
        var adv = LayoutProcessor.Arrange([dialogue, decoration, label, smallNoise], options with { ReadingMode = "ADV" }, 839, 189);
        Check(adv.Select(l => l.Text).SequenceEqual(new[] {label.Text, dialogue.Text}),
            "Short speaker label or tiny remote text bridged decoration back into ADV dialogue");
        var shorterDialogue = dialogue with {Text="「図書館へ行きます」", Bounds=new(62,62,296,41)};
        var largeNoise = decoration with {Text="ab", Confidence=.75f, Bounds=new(71,93,282,96)};
        var secondNoise = decoration with {Text="C", Confidence=.69f, Bounds=new(316,104,86,75)};
        var shortAdv = LayoutProcessor.Arrange([shorterDialogue, largeNoise, secondNoise,
            label with {Bounds=new(66,7,83,38)}, smallNoise], options with { ReadingMode = "ADV" }, 839, 189);
        Check(shortAdv.Select(l => l.Text).SequenceEqual(new[] {label.Text, shorterDialogue.Text}),
            "Large detached noise won paragraph scoring over a shorter recognized dialogue");
        var ordinaryReading = new OcrLine("かんじ", .99f, new(20,5,35,8));
        var ordinaryBody = new OcrLine("漢字", .99f, new(20,17,100,30));
        Check(LayoutProcessor.Arrange([ordinaryReading, ordinaryBody], options, 400, 300).Count == 1,
            "Recognized normal kana reading was not suppressed");
        Check(LayoutProcessor.Arrange([ordinaryReading, ordinaryBody with {Text="ABCD"}], options, 400, 300)
            .Any(l=>l.Text.Contains(ordinaryReading.Text)), "Confident Latin decoration was accepted as a kanji parent");
        Check(LayoutProcessor.Arrange([ordinaryReading with {Confidence=.5f}, ordinaryBody], options, 400, 300)
            .Any(l=>l.Text.Contains(ordinaryReading.Text)), "Uncertain small line was deleted");
        var verticalReading = ordinaryReading with { Bounds=new(54,20,8,35), Vertical=true };
        var verticalBody = ordinaryBody with { Bounds=new(20,20,30,100), Vertical=true };
        Check(LayoutProcessor.Arrange([verticalReading, verticalBody], options with {Vertical=true}, 400, 300).Count == 1,
            "Recognized vertical kana/kanji pair was not suppressed");
        IReadOnlyList<OcrLine> Arrange(OcrLine ruby, OcrLine parent, OcrOptions? selected = null) =>
            LayoutProcessor.Arrange([ruby, parent, next], selected ?? options, 2242, 1299);
        bool Retains(OcrLine ruby, OcrLine parent, OcrOptions? selected = null) => Arrange(ruby, parent, selected).Any(l => l.Text.Contains(ruby.Text));
        Check(!LayoutProcessor.IsRuby(reading.Bounds, body.Bounds, false), "Fixture no longer exercises the old size-limit miss");
        var arranged = Arrange(reading, body);
        Check(arranged.Select(l => l.Text).SequenceEqual(new[] { body.Text, next.Text }), "Expanded ruby remained or body changed");
        Check(arranged[0].Bounds == body.Bounds && arranged[0].Confidence == body.Confidence
            && arranged[1].Bounds == next.Bounds, "Supplemental filtering changed body geometry/confidence");
        Check(reading.Text == "かんじ" && body.Text == "あいうえおかきく漢字けこ、", "Input candidates were mutated");
        Check(Retains(reading, body, options with { SuppressFurigana = false }), "Disabled furigana suppression still removes text");
        Check(Retains(reading, body, options with { Language = "en" }), "Supplemental rule ran for English");
        Check(Retains(reading with { Text = "カンジ" }, body), "Katakana speaker name was discarded");
        Check(Retains(reading with { Text = "漢字" }, body), "Small kanji body text was discarded");
        Check(Retains(reading with { Text = "か" }, body), "Ambiguous single-character line was discarded");
        Check(Retains(reading with { Text = "かんじです。" }, body), "Small independent sentence was discarded");
        Check(Retains(reading with { Text = "かん じ" }, body), "Spaced/mixed-width heading was discarded");
        Check(Retains(reading with { Confidence = .6f }, body), "Weak candidate triggered supplemental filtering");
        Check(Retains(reading, body with { Confidence = .6f }), "Weak parent anchored supplemental filtering");
        Check(Retains(reading with { Bounds = new(847, 1027, 57, 30) }, body), "Comparable-height body line was discarded");
        Check(Retains(reading with { Bounds = new(847, 1000, 57, 26) }, body), "Detached heading was discarded");
        Check(Retains(reading with { Bounds = new(450, 1035, 57, 26) }, body), "Outside/left-aligned speaker label was discarded");
        Check(Retains(reading with { Bounds = new(510, 1035, 57, 26) }, body), "Kana name above a kana span was discarded");
        Check(Retains(reading, body with { Text = "あいうえおかきくけこさし、" }), "Kana-only body was used as a kanji anchor");
        Check(Retains(reading, body with { Text = "あいうえおかきく漢あけこ、" }), "Single kanji anchored the expanded-box fallback");
        Check(Retains(reading, body with { Text = "あいうえおかきく漢字ABC" }), "Mixed-width Latin parent was projected as full-width text");
        Check(Retains(reading with { Bounds = new(847, 1035, 57, 26), Vertical = true }, body with { Vertical = true }),
            "Supplemental rule changed vertical policy");
        foreach (int scale in new[] { 1, 2, 3 }) {
            OcrLine Transform(OcrLine l) => l with { Bounds = new(l.Bounds.X * scale + 27, l.Bounds.Y * scale + 19,
                l.Bounds.Width * scale, l.Bounds.Height * scale) };
            var result = LayoutProcessor.Arrange([Transform(reading), Transform(body)], options, 8000, 8000);
            Check(result.Count == 1 && result[0].Text == body.Text, "Scale/origin changed ruby classification");
        }
        // Quote-leading Japanese body, enlarged annotation >55% body height.
        // Synthetic wording with narrow-quote/kanji geometry; no game dialogue.
        var quotedBody = new OcrLine("“あいうえお漢字かきくけこさしすせそたちつてとなに", .99f, new(35,28,970,39));
        var quotedReading = new OcrLine("かんじな",.99f,new(252,9,80,22));
        Check(!LayoutProcessor.IsRuby(quotedReading.Bounds,quotedBody.Bounds,false),"Quoted fixture no longer exercises the enlarged ruby fallback");
        Check(!Retains(quotedReading,quotedBody),"Narrow opening quote blocked valid ruby exclusion");
        var quotedResult = Arrange(quotedReading,quotedBody);
        Check(quotedResult.Any(l=>l.Text==quotedBody.Text && l.Bounds==quotedBody.Bounds && l.Confidence==quotedBody.Confidence),"Quoted body text/geometry/confidence changed");
        Check(!Retains(quotedReading,quotedBody with {Text=quotedBody.Text+"”",Bounds=new(35,28,990,39)}),"Narrow closing quote blocked ruby exclusion");
        Check(Retains(quotedReading,quotedBody,options with {SuppressFurigana=false}),"Disabled suppression still removed quoted ruby");
        Check(Retains(quotedReading,quotedBody,options with {Language="en"}),"Quoted Japanese rule ran in English mode");
        Check(Retains(quotedReading,quotedBody with {Text=quotedBody.Text+"ABC"}),"Quoted mixed-width Latin body anchored ruby");
        Check(Retains(quotedReading,quotedBody with {Text=quotedBody.Text.Replace("漢字","かな")}),"Quote alone anchored kana-only body");
        Check(Retains(quotedReading with {Bounds=new(80,9,80,22)},quotedBody),"Kana label away from quoted kanji discarded");
        Check(Retains(quotedReading with {Text="かなです。"},quotedBody),"Quoted independent small sentence discarded");
        Check(Retains(quotedReading with {Confidence=.6f},quotedBody),"Uncertain quote-leading candidate removed");
        foreach (int scale in new[]{1,2}) {
            OcrLine Shift(OcrLine l)=>l with {Bounds=new(l.Bounds.X*scale+41,l.Bounds.Y*scale+200,l.Bounds.Width*scale,l.Bounds.Height*scale)};
            var result=LayoutProcessor.Arrange([Shift(quotedReading),Shift(quotedBody)],options,4000,4000);
            Check(result.Count==1 && result[0].Text==quotedBody.Text,"Quoted ruby policy changed with scale/origin");
        }
        Console.WriteLine($"PASS: {checks} conservative ruby assertions (synthetic only, no models).");
        return checks;
    }
}
