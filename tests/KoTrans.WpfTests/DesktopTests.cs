using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class DesktopTests
{
    internal static void WindowLifecycle()
    {
        var settings = new AppSettings { StudyTopmost = false };
        var study = Track(new StudyWindow(settings, resolveVoiceDirectory: () => null));
        var overlay = Track(new TranslationWindow(new Profile()));
        try
        {
            int hidden = 0; study.Hidden += () => hidden++;
            study.Render(WpfTests.Example(string.Join(" ", Enumerable.Repeat("긴 문장 스크롤 확인용 dialogue.", 100))));
            study.ShowReader(); Until(() => study.IsLoaded, "Study did not load on desktop");
            var scroll = Elements<ScrollViewer>(study).Single();
            Check(study.IsVisible && scroll.ScrollableHeight > 0, "Study desktop layout missing");
            study.ScrollRemote(true); Until(() => scroll.VerticalOffset > 0, "Remote scroll did not move visible study");
            study.Close(); Check(!study.IsVisible && hidden == 1,
                "Title-bar close should hide the resident reader without destroying it");
            study.ShowReader(); Pump(); Check(study.IsVisible && Text(study).Contains("journey"), "Resident study was not restored");
            var placement = settings.Profiles[0].StudyWindow;
            Check(placement is not null && placement.Width >= study.MinWidth && placement.Height >= study.MinHeight
                && placement.MonitorDevice.Length > 0, "Desktop study placement/monitor not remembered");
            overlay.SetResult("Synthetic source", "합성 번역", "Desktop test"); overlay.Show(); Pump();
            Check(overlay.IsVisible, "Overlay did not show"); overlay.Hide(); overlay.Show(); Pump(); Check(overlay.IsVisible, "Overlay did not restore");
            Console.WriteLine("Desktop scope: WPF lifecycle/remote scroll only. Physical input, focus, capture exclusion, audio, DPI and games are NOT verified.");
        }
        finally { Close(study); Close(overlay); }
    }
}
