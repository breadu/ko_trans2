using KoTrans.Core;
using KoTrans.Ocr;

internal static class NvlOcrTests
{
    internal static void Run()
    {
        int count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        var options = new OcrOptions("ja", "NVL", false);
        var row = new OcrLine("前半",1,new(20,30,80,24));
        var sameRow = new OcrLine("後半",1,new(130,32,80,22));
        var nextRow = new OcrLine("次の行",1,new(20,75,120,24));
        Check(NvlText.DisplayLines([nextRow,sameRow,row])=="前半 後半\n次の行","Visual Y/X order or same-row jitter produced wrong line breaks");
        Check(NvlText.DisplayLines([row with {Paragraph=2},sameRow with {Paragraph=3}])=="前半 後半","Paragraph/increment metadata created a visual line break");
        Check(NvlText.DisplayLines([row with {Text="Old warrior"},nextRow with {Text="次の文。"}])=="Old warrior\n次の文。","Original word spaces or row break lost");
        Check(NvlText.DisplayLines([])=="","Empty original created a phantom row");
        var first = new OcrLine("前の文。", 1, new(60, 50, 160, 20));
        var tail = new OcrLine("途中の文", 1, new(60, 90, 180, 20));
        var added = new OcrLine("の続き。", 1, new(60, 130, 150, 20));
        ImageFrame Frame(params OcrLine[] lines) {
            var pixels = new byte[1608 * 300];
            foreach (var line in lines) for (int y = line.Bounds.Y+3; y < line.Bounds.Y+line.Bounds.Height-3; y++)
                for (int x = line.Bounds.X+3; x < line.Bounds.X+line.Bounds.Width-3; x++) pixels[y*1608+x*4] = 230;
            return new(400,300,1608,pixels);
        }
        var tracker = new NvlOcrTracker(); var baseline = Frame(first,tail);
        tracker.Observe(baseline,new([first,tail],TimeSpan.Zero));
        var frame = Frame(first,tail,added); var region = tracker.Select(frame,false);
        Check(region is not null && region.Prefix.SequenceEqual(new[]{first}), "NVL retained prefix not reused");
        Check(region!.Area.Y <= tail.Bounds.Y && region.Area.Y+region.Area.Height > added.Bounds.Y+added.Bounds.Height, "Continuation/wrapped line excluded");
        Check(region.Area.Y > first.Bounds.Y+first.Bounds.Height, "Fixed prefix included in recognition ROI");
        var merged = NvlOcrTracker.Merge(region,new([tail,added],TimeSpan.Zero),frame,options);
        Check(merged.Lines.Count == 3 && merged.ReusedLines == 1 && merged.IncrementalArea == region.Area, "NVL merge lost text/metadata");
        Check(merged.Lines[0].Bounds == first.Bounds && merged.Lines[2].Bounds == added.Bounds, "NVL coordinates changed");
        Check(!NvlOcrTracker.NeedsFullSearch(region,new([tail,added],TimeSpan.Zero),frame), "Safe addition requested full OCR");
        Check(NvlOcrTracker.NeedsFullSearch(region,new([],TimeSpan.Zero),frame), "Empty incremental OCR accepted");
        Check(NvlOcrTracker.NeedsFullSearch(region,new([tail with { Bounds = new(region.Area.X,region.Area.Y,30,20) }],TimeSpan.Zero),frame), "Clipped incremental OCR accepted");
        var tight = new NvlOcrTracker();
        var adjacent = tail with {Bounds=tail.Bounds with {Y=first.Bounds.Y+first.Bounds.Height+3}};
        var tightFrame = Frame(first,adjacent);
        tight.Observe(tightFrame,new([first,adjacent],TimeSpan.Zero));
        Check(tight.Select(tightFrame,false) is null,"A predictably clipped top edge still requested partial OCR before a full retry");
        tight.Observe(baseline,new([first,tail],TimeSpan.Zero));
        Check(tight.Select(frame,false) is not null,"Conservative edge prediction permanently disabled a later safe partial search");
        var rejected = new NvlOcrTracker();
        rejected.Observe(baseline,new([first,tail],TimeSpan.Zero));
        var rejectedRegion = rejected.Select(frame,false)!;
        rejected.RejectRegion(rejectedRegion);
        rejected.Observe(frame,new([first,tail,added],TimeSpan.Zero));
        Check(rejected.Select(frame,false) is null,"Failed NVL crop retried the same retained-prefix boundary");
        Check(rejected.Select(frame,true) is null && rejected.Select(frame,false) is null,"Full verification reopened a known failed crop");
        rejected.Observe(frame,new([first with {Bounds=first.Bounds with {X=61}},tail,added],TimeSpan.Zero));
        Check(rejected.Select(frame,false) is null,"Detector geometry jitter reopened a failed crop for unchanged text");
        rejected.Observe(frame,new([first,tail with {Text="完成した文。"},added],TimeSpan.Zero));
        Check(rejected.Select(frame,false) is not null,"New completed prefix could not resume incremental OCR");
        rejected.RejectRegion(rejected.Select(frame,false)!);
        rejected.Reset();
        rejected.Observe(baseline,new([first,tail],TimeSpan.Zero));
        Check(rejected.Select(frame,false) is not null,"Session/page reset retained a failed crop");
        rejected.RejectRegion(rejected.Select(frame,false)!);
        Check(rejected.Select(new(200,200,800,new byte[160000]),false) is null && rejected.PageBreakPending,"Resize did not reset the rejected crop");
        rejected.Reset();
        rejected.Observe(baseline,new([first,tail],TimeSpan.Zero));
        rejected.RejectRegion(rejected.Select(frame,false)!);
        var newBackground = Enumerable.Repeat((byte)255,1608*300).ToArray();
        Check(rejected.Select(new(400,300,1608,newBackground),false) is null && rejected.PageBreakPending,
            "Rejected crop bypassed the existing page-change detection");
        var changed = frame.Pixels.ToArray(); changed[first.Bounds.Y*1608+(first.Bounds.X+5)*4] = 255;
        Check(tracker.Select(new(400,300,1608,changed),false) is null, "Changed fixed glyph reused");
        Check(tracker.Select(frame,true) is null, "Explicit full search cropped");
        Check(tracker.Select(new(200,200,800,new byte[160000]),false) is null && tracker.PageBreakPending, "Resize retained prior page");
        Check(tracker.ConsumePageBreak() && !tracker.ConsumePageBreak(), "Page break was not one-shot");
        tracker.Observe(baseline,new([first,tail],TimeSpan.Zero));
        tracker.Observe(Frame(),new([],TimeSpan.Zero));
        Check(tracker.PageBreakPending && tracker.Select(frame,false) is null, "Blank page retained OCR");
        tracker.Reset(); Check(!tracker.PageBreakPending, "Reset kept pending page");
        Check(!NvlOcrTracker.Supports(options with { Vertical=true },OcrCapabilities.Paddle)
            && !NvlOcrTracker.Supports(options with { ReadingMode="ADV" },OcrCapabilities.Paddle), "Other modes opted into horizontal NVL");
        var marker = new OcrLine("K",.9f,new(190,10,10,10));
        var native = new OcrResult([first with { Bounds=new(10,10,160,20) }],TimeSpan.Zero) { Candidates=[first with { Bounds=new(10,10,160,20) },marker] };
        var restored = OcrRegionTracker.Restore(native,new(50,40,250,80),baseline,options);
        Check(restored.Lines.Count==1 && restored.Candidates!.Count==2, "ROI restoration reintroduced filtered cursor");
        var stable = new NvlOcrTracker(); stable.Observe(baseline,new([first,tail],TimeSpan.Zero));
        var jitter=tail with { Text="検出枠による別の句読点。", Bounds=new(59,89,183,22) };
        Check(stable.Stabilize(baseline,new([first,jitter],TimeSpan.Zero)).Lines[1].Text==tail.Text,"Identical complete row pixels oscillated with detector crop");
        var changedBody=baseline.Pixels.ToArray(); changedBody[95*1608+70*4]++;
        Check(stable.Stabilize(new(400,300,1608,changedBody),new([first,jitter],TimeSpan.Zero)).Lines[1].Text==jitter.Text,"One-pixel real body edit was ignored");
        var appended=tail with { Text="本当の追加文字。",Bounds=new(60,90,220,20) };
        Check(stable.Stabilize(Frame(first,appended),new([first,appended],TimeSpan.Zero)).Lines[1].Text==appended.Text,"Added glyph outside prior box was hidden");
        stable.Observe(baseline,new([first,tail with {Confidence=.5f}],TimeSpan.Zero));
        Check(stable.Stabilize(baseline,new([first,jitter],TimeSpan.Zero)).Lines[1].Text==jitter.Text,"Low-confidence prior OCR overrode a better read");
        stable.Reset(); Check(stable.Stabilize(baseline,new([jitter],TimeSpan.Zero)).Lines.Single()==jitter,"Reset retained prior row text");
        stable.Observe(baseline,new([first,tail],TimeSpan.Zero));
        var search1=stable.Select(baseline,false)!;
        stable.Observe(baseline,new([first,tail with { Bounds=new(62,92,170,18) }],TimeSpan.Zero));
        var search2=stable.Select(baseline,false)!;
        Check(search2.Area.X<=search1.Area.X && search2.Area.Y<=search1.Area.Y && search2.Area.X+search2.Area.Width>=search1.Area.X+search1.Area.Width
            && search2.Area.Y+search2.Area.Height>=search1.Area.Y+search1.Area.Height,"Detector jitter shrank the same unfinished block search");
        var left = new OcrLine("左側の本文。",.99f,new(17,7,456,35),Paragraph:4);
        var right = new OcrLine("右側の続き」",.99f,new(537,3,233,41),Paragraph:1);
        var lower = new OcrLine("次の段落",.99f,new(13,40,444,40),Paragraph:4);
        var splitRows=NvlOcrTracker.ArrangeRows(new([right,left,lower],TimeSpan.Zero),options);
        var joinedRows=NvlOcrTracker.ArrangeRows(new([left with {Text=left.Text+right.Text,Bounds=new(17,3,753,41)},lower],TimeSpan.Zero),options);
        Check(splitRows.Text==joinedRows.Text && splitRows.Lines.Count==2,"Wide gap/Y jitter reversed same-row NVL fragments");
        Check(splitRows.Lines[0].Text==left.Text+right.Text && splitRows.Lines[1].Text==lower.Text,"Paragraph IDs reordered physical rows");
        Check(NvlText.MergeVisualRows([left with {Text="Old warrior"},right with {Text="comes here"}],"en").Single().Text=="Old warrior comes here","English fragment spacing was lost");
        var vertical=left with {Vertical=true};
        Check(NvlText.MergeVisualRows([vertical],"ja").Single()==vertical,"Mixed/vertical geometry was rewritten");
        Check(NvlOcrTracker.ArrangeRows(new([right,left],TimeSpan.Zero),options with {ReadingMode="ADV"}).Lines.Count==2,"ADV layout opted into NVL row merging");
        tracker.Reset(); tracker.Observe(baseline,new([first,tail with {Bounds=new(60,65,180,24)}],TimeSpan.Zero));
        var overlapRegion=tracker.Select(baseline,false);
        Check(overlapRegion is not null && overlapRegion.Prefix.Count==0 && overlapRegion.Area.Y<=first.Bounds.Y,"Overlapping detector rows froze a prefix through the next row");
        // Accepted rows can contain a tall union of text/background fragments.
        // Reapplying ruby detection to that union must not erase the frozen body.
        var retained = new[] {
            new OcrLine("最初の本文。",.99f,new(50,40,300,20)),
            new OcrLine("次の本文。",.99f,new(50,80,300,20)),
            new OcrLine("最後の確定文。",.99f,new(50,120,300,20)) };
        var tall = new OcrLine("続きの本文",.99f,new(60,160,280,140));
        var tallFrame = new ImageFrame(400,400,1600,new byte[640000]);
        var tallRegion = new NvlOcrRegion(new(0,141,400,259),retained);
        var acceptedTail = new OcrResult([tall],TimeSpan.Zero) {
            Candidates=[tall,new("かんじ",.99f,new(70,143,40,10))] };
        foreach(bool suppress in new[]{true,false}) {
            var combined=NvlOcrTracker.Merge(tallRegion,acceptedTail,tallFrame,options with {SuppressFurigana=suppress});
            Check(combined.Lines.Select(l=>l.Text).SequenceEqual(retained.Append(tall).Select(l=>l.Text)),
                "NVL merge treated already accepted body rows as ruby of an enlarged row");
            Check(combined.ReusedLines==retained.Length && combined.IncrementalArea==tallRegion.Area,
                "NVL merge changed reuse metadata");
            Check(combined.Lines.All(l=>l.Text!="かんじ") && combined.Candidates!.Count==5,
                "NVL merge restored a filtered diagnostic-only reading");
        }
        Console.WriteLine($"PASS: {count} NVL OCR assertions. Synthetic pixels; no model or screen access.");
    }
}
