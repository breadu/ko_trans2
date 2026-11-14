using KoTrans.Core;
using KoTrans.Ocr;
using OpenCvSharp;

internal static class ContinuationMarkerTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        using var image = new Mat(120, 500, MatType.CV_8UC3, Scalar.Black);
        var body = new OcrLine("これは本文です。", .99f, new(20, 30, 300, 42));
        var marker = new OcrLine("K", .95f, new(333, 43, 25, 20));
        var options = new OcrOptions("ja", "NVL", false);
        IReadOnlyList<OcrLine> Run(OcrLine? candidate = null, OcrLine? parent = null, OcrOptions? opt = null) =>
            ContinuationMarker.Exclude([parent ?? body, candidate ?? marker], image, opt ?? options);
        void Paint(Scalar color, Scalar? bodyColour = null) {
            image.SetTo(Scalar.Black);
            foreach (int x in new[] { 215, 260 }) Cv2.Rectangle(image, new(x, 38, 20, 25), bodyColour ?? new Scalar(180, 220, 180), -1);
            Cv2.Rectangle(image, new(337, 46, 17, 13), color, -1);
        }
        Paint(new Scalar(230, 190, 140));
        Check(Run().SequenceEqual(new[] { body }), "Different-colour continuation marker K remained");
        Check(Run(marker with { Text = "V" }).Count == 1, "Different-colour continuation marker V remained");
        Check(Run(marker with { Text = "A" }).Count == 1, "Marker handling was hardcoded to a K/V blacklist");
        foreach (char digit in "0123456789０１２３４５６７８９") Check(Run(marker with { Text = digit.ToString() }).Count == 1, "Small differently-coloured page marker digit remained");
        foreach (char digit in "0123456789０１２３４５６７８９") Check(Run(marker with { Text = digit.ToString() }, opt: options with { ReadingMode = "ADV" }).Count == 1, "ADV numeric page marker remained");
        Check(Run(marker with { Text = "68" }).Count == 2, "Independent two-digit number was discarded without marker evidence");
        Check(Run(parent: body with { Text = "数字68は本文です。" }).Single().Text == "数字68は本文です。", "Body digits were stripped");
        Check(Run(marker with { Text = "6", Bounds = new(333,31,25,40) }).Count == 2, "Full-height number was removed");
        Check(Run(marker with { Text = "8", Bounds = new(310,43,25,20) }).Count == 2, "Overlapping number was removed");
        Check(Run(parent: body with { Text = "本当の文字はK。" }).Single().Text == "本当の文字はK。", "Actual K in body text was edited");
        Check(Run(marker with { Text = "かな" }).Count == 2, "Small Japanese body text was discarded");
        Check(Run(marker with { Text = "KV" }).Count == 2, "Independent multi-letter text was discarded");
        Check(Run(marker with { Bounds = new(333, 31, 25, 40) }).Count == 2, "Normal-sized trailing letter was discarded");
        Check(Run(marker with { Bounds = new(310, 43, 25, 20) }).Count == 2, "Overlapping/merged text box was trimmed");
        Check(Run(parent: body with { Text = "これは本文です" }).Count == 2, "Unfinished Japanese sentence lost a letter");
        Check(Run(parent: body with { Confidence = .5f }).Count == 2, "Weak parent justified removing a candidate");
        Check(Run(opt: options with { Language = "en" }).Count == 2, "English letter was discarded");
        Check(Run(opt: options with { ReadingMode = "ADV" }).Count == 2, "ADV policy was changed");
        Check(Run(opt: options with { Vertical = true }).Count == 2, "Vertical policy was changed");
        foreach (var color in new[] { Scalar.White, new Scalar(180, 220, 180), new Scalar(90, 110, 90) }) {
            Paint(color); Check(Run().Count == 2, "Matching or uncertain-colour trailing letter was discarded");
            Check(Run(marker with { Text = "6" }).Count == 2, "Matching/uncertain-colour trailing digit was discarded");
        }
        foreach (var color in new[] { new Scalar(40, 50, 230), new Scalar(30, 190, 230), new Scalar(220, 40, 200), new Scalar(70, 60, 50) }) {
            Paint(color); Check(Run().Count == 1, "Marker colour was restricted to blue: " + color);
        }
        Paint(new Scalar(214, 188, 167), new Scalar(160, 210, 160));
        Check(Run().Count == 1, "Muted, distinctly different marker colour remained");
        Paint(new Scalar(40, 50, 230), Scalar.White);
        Check(Run().Count == 1, "Red marker beside white body was not excluded");
        Paint(new Scalar(150, 150, 150), Scalar.White);
        Check(Run().Count == 2, "Brightness-only difference was treated as a marker colour");
        Paint(new Scalar(230, 190, 140));
        foreach (int x in new[] { 215, 260 }) Cv2.Rectangle(image, new(x, 38, 20, 25), new Scalar(230, 190, 140), -1);
        Check(Run().Count == 2, "Blue body lettering was treated as a different-colour marker");
        image.SetTo(new Scalar(230, 190, 140)); Check(Run().Count == 2, "Blue background was treated as a marker");
        image.SetTo(Scalar.White);
        foreach (int x in new[] { 215, 260 }) Cv2.Rectangle(image, new(x, 38, 20, 25), new Scalar(130, 70, 35), -1);
        Cv2.Rectangle(image, new(337, 46, 17, 13), new Scalar(30, 30, 180), -1);
        Check(Run().Count == 1, "Different-colour dark marker on a light background remained");
        Paint(new Scalar(40,80,230),Scalar.White);
        var wideMarker=marker with {Text="123",Confidence=.6f,Bounds=new(333,43,32,20)};
        var diagnostics=new List<OcrMarkerObservation>();
        var observed=ContinuationMarker.Exclude([body,wideMarker with {Text="VO"}],image,options,diagnostics.Add);
        Check(observed.SequenceEqual(Run(wideMarker with {Text="VO"})),"Diagnostics changed marker filtering");
        Check(diagnostics.Single().Decision is OcrMarkerDecision.ExcludedDifferentColour or OcrMarkerDecision.ExcludedChromaticCore
            && diagnostics[0].TrailingLatinDigits==2 && diagnostics[0].ParentBounds==body.Bounds,"Marker exclusion lacks numeric evidence");
        diagnostics.Clear();
        ContinuationMarker.Exclude([body,wideMarker with {Text="6",Bounds=new(333,31,32,40)}],image,options,diagnostics.Add);
        Check(diagnostics.Single().Decision==OcrMarkerDecision.GeometryMismatch,"Preserved marker has no geometry rejection reason");
        diagnostics.Clear();
        ContinuationMarker.Exclude([body with {Text="これは途中"},wideMarker with {Text="VO"}],image,options,diagnostics.Add);
        Check(diagnostics.Single().Decision==OcrMarkerDecision.NoCompletedParent,"Missing completed parent was not diagnosed");
        var merged=OcrMarkerDiagnostics.Describe(body with {Text="これは本文です。VO"},OcrMarkerStage.JoinedTail,OcrMarkerDecision.NoUniqueTailComponent);
        Check(merged.TrailingLatinDigits==2 && !merged.ClosingPunctuation,"Merged tail shape is not diagnosed without recording text");
        var json=System.Text.Json.JsonSerializer.Serialize(merged);
        Check(!json.Contains("本文") && !json.Contains("VO") && !typeof(OcrMarkerObservation).GetProperties().Any(p=>p.PropertyType==typeof(string)),"Marker evidence contains recognized text");
        var roundtrip=System.Text.Json.JsonSerializer.Deserialize<OcrResult>(System.Text.Json.JsonSerializer.Serialize(new OcrResult([],TimeSpan.Zero) {MarkerDiagnostics=[merged],MarkerObservations=1}));
        Check(roundtrip?.MarkerDiagnostics?.Single()==merged && roundtrip.MarkerObservations==1,"Worker result serialization lost marker evidence");
        var request=new DirectMlRequest(Guid.NewGuid(),DirectMlProtocol.Version,"recognize",options with {CollectMarkerDiagnostics=true});
        Check(!options.CollectMarkerDiagnostics && System.Text.Json.JsonSerializer.Deserialize<DirectMlRequest>(System.Text.Json.JsonSerializer.Serialize(request))!.Options.CollectMarkerDiagnostics,
            "Marker opt-in is enabled by default or lost by GPU worker transport");
        Check(Run(wideMarker).Count==1,"One compact coloured marker decoded as several characters remained");
        Check(Run(wideMarker with {Text="VO"}).Count==1,"VO-shaped marker was not treated by existing visual evidence");
        Check(Run(wideMarker with {Text="VO",Bounds=new(333,31,32,40)}).Count==2,"Normal-sized VO was discarded");
        Check(Run(wideMarker with {Text="VO"},parent:body with {Text="これはVOの説明です。"}).Single().Text=="これはVOの説明です。","Real VO inside body was stripped");
        Check(Run(wideMarker with {Text="VO"},parent:body with {Text="これは途中…"}).Count==2,"Uncertain unfinished sentence lost real VO");
        Check(Run(wideMarker,opt:options with {ReadingMode="ADV"}).Count==1,"ADV short marker noise remained");
        Check(Run(wideMarker,parent:body with {Text="これはまだ途中"}).Count==2,"Incomplete body lost a real suffix");
        Check(ContinuationMarker.SeparateTail(image,new(20,30,350,42)) is { } split && split.X+split.Width>body.Bounds.X+body.Bounds.Width
            && split.X+split.Width<337,"Joined detector box did not propose a body-preserving marker split");
        Check(ContinuationMarker.SeparateTail(image,new(333,43,32,20)) is null,"Small standalone text was split without a long body");
        Cv2.Rectangle(image,new(330,46,4,10),Scalar.White,-1);
        Check(ContinuationMarker.SeparateTail(image,new(20,30,350,42)) is null,"Real punctuation in the removed margin was cropped");
        Paint(Scalar.White,Scalar.White);
        Check(Run(wideMarker with {Text="VO"}).Count==2,"Same-colour letters lost the conservative visual protection");
        Check(ContinuationMarker.SeparateTail(image,new(20,30,350,42)) is null,"Neutral real suffix was split as a coloured marker");
        Paint(new Scalar(40,80,230),Scalar.White);
        Cv2.Rectangle(image,new(333,43,35,23),Scalar.Black,-1);
        Cv2.Rectangle(image,new(337,46,6,13),new Scalar(40,80,230),-1);
        Cv2.Rectangle(image,new(350,46,6,13),new Scalar(40,80,230),-1);
        Check(Run(wideMarker with {Text="68",Confidence=.99f}).Count==2,"Two independently coloured numeral glyphs were discarded");
        Console.WriteLine($"PASS: {checks} continuation-marker assertions (synthetic pixels only).");
        return checks;
    }
}
