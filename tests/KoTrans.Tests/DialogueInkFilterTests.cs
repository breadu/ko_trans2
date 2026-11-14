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
        using var noInk = new Mat(180,400,MatType.CV_8UC3,new Scalar(100,110,120));
        Check(DialogueInkFilter.Exclude(noInk,input,options).Contains(noise), "OCR text alone established bright dialogue");
        Cv2.Rectangle(noInk,new(20,16,330,22),Scalar.White,-1);
        Check(DialogueInkFilter.Exclude(noInk,input,options).Contains(noise), "A broad highlight was mistaken for a glyph row");
        using var whiteNumber = image.Clone();
        Cv2.PutText(whiteNumber,"7",new(42,102),HersheyFonts.HersheySimplex,.8,Scalar.White,2);
        Check(DialogueInkFilter.Exclude(whiteNumber,input,options).Contains(noise), "A real white numeric row was erased");
        using var colouredNumber = image.Clone();
        Cv2.PutText(colouredNumber,"7",new(42,102),HersheyFonts.HersheySimplex,.8,new Scalar(20,20,240),2);
        Check(DialogueInkFilter.Exclude(colouredNumber,input,options).Contains(noise), "A real coloured numeric row was erased");
        using var darkNumber = image.Clone();
        Cv2.Rectangle(darkNumber,new(40,78,30,30),new Scalar(160,160,160),-1);
        Cv2.PutText(darkNumber,"7",new(42,102),HersheyFonts.HersheySimplex,.8,Scalar.Black,2);
        Check(DialogueInkFilter.Exclude(darkNumber,input,options).Contains(noise), "A dark high-contrast numeric row was erased");
        Console.WriteLine($"Dialogue ink filtering: {count} assertions passed (synthetic pixels).");
    }
}
