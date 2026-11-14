using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class OpacitySliderTests
{
    internal static void TrackAndKeyboard()
    {
        string dir = FeedbackTests.DirectoryPath(); var settings = new AppSettings(); settings.Validate();
        var main = Program.Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => []));
        main.ApplyLoadedSettings(settings); main.ShowProfileSettingsPage(3);
        var overlay = Program.Track(main.CreateOverlayWindow()); Program.Track(overlay.Chrome);
        try {
            var slider = Named<Slider>(main, "OpacitySlider");
            Check(slider.IsMoveToPointEnabled && slider.Minimum == .2 && slider.Maximum == 1,
                "Opacity track uses page-sized clicks or changed the supported range");
            foreach (double width in new[] { 1060d, 1400d }) {
                Layout(main, width, 800); slider.ApplyTemplate();
                var track = (Track)slider.Template.FindName("PART_Track", slider);
                Check(track.ActualWidth > track.Thumb.ActualWidth, "Opacity track has no usable travel");
                foreach (double fraction in new[] { .25, .5, .75 }) {
                    slider.UpdateLayout();
                    // Real WPF track geometry used by IsMoveToPointEnabled; no native mouse input.
                    double x = track.Thumb.ActualWidth / 2 + fraction * (track.ActualWidth - track.Thumb.ActualWidth);
                    slider.Value = track.ValueFromPoint(new Point(x, track.ActualHeight / 2));
                    double expected = .2 + .8 * fraction;
                    Check(Math.Abs(slider.Value - expected) < .001 && Math.Abs(settings.DefaultProfile.OverlayOpacity - expected) < .001
                        && Math.Abs(overlay.Opacity - expected) < .001, $"Track position did not update profile and overlay opacity: {width}/{fraction}, value={slider.Value}, profile={settings.DefaultProfile.OverlayOpacity}, overlay={overlay.Opacity}, expected={expected}");
                }
            }
            slider.Value = .5; Slider.IncreaseLarge.Execute(null, slider);
            Check(Math.Abs(slider.Value - .58) < .001, "Page increment jumps to maximum opacity");
            Slider.DecreaseSmall.Execute(null, slider);
            Check(Math.Abs(slider.Value - .57) < .001, "Arrow increment is outside the opacity scale");
            foreach (string name in new[] { "FontSlider", "OriginalFontSlider" }) {
                var font = Named<Slider>(main, name);
                Check(font.IsMoveToPointEnabled, "Font track clicks move only one step");
                font.ApplyTemplate();
                var track = (Track)font.Template.FindName("PART_Track", font);
                foreach (double fraction in new[] { .2, .8 }) {
                    font.UpdateLayout();
                    double x = track.Thumb.ActualWidth / 2 + fraction * (track.ActualWidth - track.Thumb.ActualWidth);
                    font.Value = track.ValueFromPoint(new Point(x, track.ActualHeight / 2));
                    double profileSize = name == "FontSlider" ? settings.DefaultProfile.OverlayFontSize : settings.DefaultProfile.OverlayOriginalFontSize;
                    Check(Math.Abs(font.Value - (12 + 48 * fraction)) < .05 && profileSize == font.Value,
                        $"Font track position did not update {name}: fraction={fraction}, slider={font.Value}, profile={profileSize}");
                }
            }
            var save = main.SaveCurrentProfileAsync(); Complete(save);
            Check(save.Result, "Opacity setting did not save");
            var loaded = new SettingsStore(dir).LoadAsync(); Complete(loaded);
            Check(Math.Abs(loaded.Result.DefaultProfile.OverlayOpacity - .57) < .001, "Opacity value did not survive settings persistence");
        } finally { Close(overlay); Close(overlay.Chrome); Close(main); Directory.Delete(dir, true); }
    }
}
