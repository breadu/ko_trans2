using KoTrans.Core;
using KoTrans.Ocr;
using OpenCvSharp;

internal static class DialogueInkFilterTests
{
    internal static void Run()
    {
        int count = 0;
        void Check(bool valid, string message) { if (!valid) throw new Exception(message); count++; }
        using var image = new Mat(180, 400, MatType.CV_8UC3, new Scalar(100, 110, 120));
        for (int x = 20; x < 350; x += 25) Cv2.Rectangle(image, new(x, 16, 12, 22), Scalar.White, -1);
        var options = new OcrOptions("ja", "ADV", false);
        var body = new OcrLine("「明日は静かな公園を歩きましょう。」", .99f, new(10, 10, 360, 34));
        var noise = new OcrLine("7", .62f, new(40, 78, 30, 30));
        var input = new List<OcrLine> {body, noise};
        Check(DialogueInkFilter.Exclude(image, input, options).SequenceEqual(new[] {body}),
            "Dim detached scenery was appended as a numeric row");
        Check(input.Count == 2, "Filtering mutated the diagnostic input");
        foreach (string text in new[] {"C", "V", "9", "A2"})
            Check(DialogueInkFilter.Exclude(image, [body, noise with {Text=text}], options).SequenceEqual(new[] {body}),
                "The background check depended on a specific recognized character");
        foreach (var mode in new[] {options with {Language="en"}, options with {ReadingMode="NVL"},
            options with {Vertical=true}, options with {KeepLocalParagraph=true}})
            Check(DialogueInkFilter.Exclude(image, input, mode).SequenceEqual(input), "Unrelated layout was changed");
        foreach (var genuine in new[] {noise with {Confidence=.8f}, noise with {Text="あ"}, noise with {Text="…"},
            noise with {Text="123"}, noise with {Bounds=new(40,14,30,30)}, noise with {Bounds=new(380,78,20,30)},
            noise with {Bounds=new(40,130,30,30)}, noise with {Bounds=new(-1,78,30,30)}})
            Check(DialogueInkFilter.Exclude(image, [body,genuine], options).Contains(genuine),
                "Confident, inline, unrelated or non-Latin dialogue was discarded");
        Check(DialogueInkFilter.Exclude(image, [noise], options).Contains(noise), "No dialogue anchor erased a result");
        Check(DialogueInkFilter.Exclude(image, [body with {Confidence=.89f},noise], options).Contains(noise),
            "Uncertain dialogue anchored an exclusion");
        var secondRow = new OcrLine("次の行", .96f, new(80,78,150,30));
        Check(DialogueInkFilter.Exclude(image, [body,noise,secondRow], options).Contains(noise),
            "A weak number attached to a genuine second row was lost");
        var duplicate = new OcrLine("1", .9f, new(50,99,16,10));
        var weakDigit = noise with {Text="1",Confidence=.78f};
        var filtered = DialogueInkFilter.Exclude(image,[body,weakDigit,duplicate],options);
        Check(!filtered.Contains(weakDigit) && filtered.Contains(duplicate),
            "A confident nested fragment protected the larger dim scenery patch");
        Check(LayoutProcessor.Arrange(filtered,options,400,180).Single().Text==body.Text,
            "Nested detector fragments were appended as a false numeric dialogue row");
        foreach (float confidence in new[] {.79f,.81f,.99f}) {
            var confident = weakDigit with {Confidence=confidence};
            var result = DialogueInkFilter.Exclude(image,[body,confident,duplicate],options);
            Check(!result.Contains(confident) && result.Contains(duplicate)
                && LayoutProcessor.Arrange(result,options,400,180).Single().Text==body.Text,
                "Nested background evidence depended on the recognition confidence boundary");
        }
        var separateSmall = duplicate with {Bounds=new(80,99,16,10)};
        Check(DialogueInkFilter.Exclude(image,[body,weakDigit,separateSmall],options).Contains(weakDigit),
            "An independent small neighbouring glyph lost its row protection");
        var sameHeight = duplicate with {Bounds=noise.Bounds};
        Check(DialogueInkFilter.Exclude(image,[body,weakDigit,sameHeight],options).Contains(weakDigit),
            "A normal-height confident row was treated as a tiny nested fragment");
        using var noInk = new Mat(180,400,MatType.CV_8UC3,new Scalar(100,110,120));
        Check(DialogueInkFilter.Exclude(noInk,input,options).Contains(noise), "OCR text alone established bright dialogue");
        Cv2.Rectangle(noInk,new(20,16,330,22),Scalar.White,-1);
        Check(DialogueInkFilter.Exclude(noInk,input,options).Contains(noise), "A broad highlight was mistaken for a glyph row");
        using var whiteNumber = image.Clone();
        Cv2.PutText(whiteNumber,"7",new(42,102),HersheyFonts.HersheySimplex,.8,Scalar.White,2);
        Check(DialogueInkFilter.Exclude(whiteNumber,input,options).Contains(noise), "A real white numeric row was erased");
        Check(DialogueInkFilter.Exclude(whiteNumber,[body,weakDigit,duplicate],options).Contains(weakDigit),
            "A genuine numeric row with overlapping detections lost its visible glyphs");
        var strongDigit = weakDigit with {Confidence=.99f};
        Check(DialogueInkFilter.Exclude(whiteNumber,[body,strongDigit,duplicate],options).Contains(strongDigit),
            "A confident visible digit with duplicate detections was discarded");
        using var colouredNumber = image.Clone();
        Cv2.PutText(colouredNumber,"7",new(42,102),HersheyFonts.HersheySimplex,.8,new Scalar(20,20,240),2);
        Check(DialogueInkFilter.Exclude(colouredNumber,input,options).Contains(noise), "A real coloured numeric row was erased");
        using var darkNumber = image.Clone();
        Cv2.Rectangle(darkNumber,new(40,78,30,30),new Scalar(160,160,160),-1);
        Cv2.PutText(darkNumber,"7",new(42,102),HersheyFonts.HersheySimplex,.8,Scalar.Black,2);
        Check(DialogueInkFilter.Exclude(darkNumber,input,options).Contains(noise), "A dark high-contrast numeric row was erased");
        Check(DialogueInkFilter.Exclude(darkNumber,[body,strongDigit,duplicate],options).Contains(strongDigit),
            "A high-contrast dark digit with duplicate detections was discarded");
        Console.WriteLine($"Dialogue ink filtering: {count} assertions passed (synthetic pixels).");
    }
}
