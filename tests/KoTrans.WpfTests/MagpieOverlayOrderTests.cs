using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class MagpieOverlayOrderTests
{
    internal static void NotificationsAndLifetime()
    {
        var profile = new Profile { CaptureMode = CaptureMode.Window, OverlayClickThrough = true };
        string originalProfile = JsonSerializer.Serialize(profile);
        var overlay = Track(new TranslationWindow(profile)); Track(overlay.Chrome);
        try {
            Layout(overlay, 800, 240); Layout(overlay.Chrome, 800, 240);
            Check(overlay.Topmost && overlay.Chrome.Topmost && !overlay.ShowActivated && !overlay.Chrome.ShowActivated,
                "Overlay topmost/no-activation defaults changed");
            Check(new WindowInteropHelper(overlay).Handle == 0 && new WindowInteropHelper(overlay.Chrome).Handle == 0,
                "Constructing/layout of overlay created native windows for Magpie");

            var native = new FakeWindows(); var queue = new Queue<Action>();
            var receiverParameters = MagpieNotificationReceiver.Parameters();
            Check(receiverParameters.ParentWindow == 0 && (receiverParameters.WindowStyle & unchecked((int)0x80000000)) != 0
                && (receiverParameters.WindowStyle & 0x50000000) == 0
                && (receiverParameters.ExtendedWindowStyle & 0x08000080) == 0x08000080,
                "Magpie broadcast receiver must be an invisible unowned non-activating popup, not a child/message-only HWND");
            var game = new WindowTarget(10, 42, 123, "Synthetic game", "synthetic");
            var state = new OverlayOrderContext(11, 12, true, CaptureMode.Window, game, null);
            using var order = new MagpieOverlayOrder(native, () => state, queue.Enqueue);
            void PumpOrder() { while (queue.TryDequeue(out var action)) action(); }
            void Notify() { native.Deliver(1, 30); PumpOrder(); }
            void Reset() { native.Positions.Clear(); native.Order = [30, 12, 11]; }
            void NoPosition(string reason) { PumpOrder(); Check(native.Positions.Count == 0, reason); }

            order.Shown(); Check(queue.Count == 0 && native.Listens == 0, "Native work started before source initialization");
            order.Attach(11); order.Attach(11);
            Check(native.Listens == 1, "Notification registered more than once for the same lifetime");
            Check(native.Receiver is not null, "Native notification callback was not connected to controller");
            Check(!order.HandleMessage(0x21, 1, 30), "Magpie hook consumed ordinary mouse-activation message");
            for (int i = 0; i < 5; i++) Check(order.HandleMessage(FakeWindows.Message, 1, 30), "Scaling-start notification not consumed");
            Check(queue.Count == 1 && native.Positions.Count == 0, "Notification burst was not deferred/coalesced");
            PumpOrder();
            Check(native.Positions.Select(p => p.Window).SequenceEqual(new nint[] { 11, 12 }), "Body and input frame were not raised in order");
            Check(native.Positions.All(p => p.After == -1 && (p.Flags & 0x0010) != 0 && (p.Flags & 0x0200) != 0
                && (p.Flags & 0x0003) == 0x0003 && (p.Flags & (0x0040 | 0x0080 | 0x0004)) == 0),
                "Z-order operation could activate, move, resize, show/hide, reorder owner, or skip the requested order");
            Check(native.Order.SequenceEqual(new nint[] { 12, 11, 30 }) && native.Foreground == 10,
                "Chrome/body order or game foreground was lost");
            native.Positions.Clear(); Notify(); NoPosition("Already-correct order was disturbed");
            foreach (nint eventId in new nint[] { 0, 2, 3, 99 }) order.HandleMessage(FakeWindows.Message, eventId, 30);
            NoPosition("Exit/resize/unknown event changed normal topmost policy");
            Reset(); order.HandleMessage(FakeWindows.Message, 1, 30); order.HandleMessage(FakeWindows.Message, 0, 1);
            NoPosition("Queued raise survived game losing focus");
            order.HandleMessage(FakeWindows.Message, 1, 30); order.HandleMessage(FakeWindows.Message, 0, 0);
            NoPosition("Queued raise survived scaling ending");
            order.HandleMessage(FakeWindows.Message, 1, 30); order.Hidden();
            NoPosition("Queued raise survived hiding the subtitle");
            order.Shown(); PumpOrder();
            Check(native.Positions.Count == 2 && native.LastCandidate == 0, "Showing overlay during existing scaling did not discover it");
            Reset(); order.HandleMessage(FakeWindows.Message, 1, 30); order.Hidden(); order.Shown(); PumpOrder();
            Check(native.Positions.Count == 2, "Stale queued callback canceled a newer show request");

            Reset(); native.Present = false; Notify(); NoPosition("Ordinary game/no Magpie changed overlay order"); native.Present = true;
            native.Foreground = 99; Notify(); NoPosition("Alt-Tab/main/another application's foreground caused raising"); native.Foreground = 10;
            native.Source = 99; Notify(); NoPosition("Unrelated game's Magpie session changed overlay order"); native.Source = 10;
            order.HandleMessage(FakeWindows.Message, 1, 31); NoPosition("Stale or forged output handle accepted");
            order.HandleMessage(FakeWindows.Message, 1, 0); NoPosition("Missing output handle treated as scaling-start");
            native.ValidTarget = false; Notify(); NoPosition("Destroyed/reused target identity accepted"); native.ValidTarget = true;
            state = state with { Target = null }; Notify(); NoPosition("Unconnected window profile followed an unrelated game"); state = state with { Target = game };
            state = state with { Visible = false }; Notify(); NoPosition("Hidden/minimized/closed subtitle was raised"); state = state with { Visible = true };
            native.Hidden.Add(11); Notify(); NoPosition("Native hidden/minimized subtitle was raised"); native.Hidden.Clear();
            order.HandleMessage(FakeWindows.Message, 1, 30); native.Foreground = 99;
            NoPosition("Foreground was not rechecked when deferred work ran"); native.Foreground = 10;
            order.HandleMessage(FakeWindows.Message, 1, 30); state = state with { Target = game with { Handle = 99 } };
            NoPosition("Target switch was not rechecked when deferred work ran"); state = state with { Target = game };

            native.Hidden.Add(12); Notify();
            Check(native.Positions.Select(p => p.Window).SequenceEqual(new nint[] { 11 }), "Hidden input frame was raised/shown"); native.Hidden.Clear();
            Reset(); native.PositionFails = true; Notify();
            Check(native.Positions.Count == 1, "Native positioning failure caused retries or separate chrome activation"); native.PositionFails = false;

            Reset(); state = state with { Mode = CaptureMode.Region, Target = null, CaptureArea = new(10, 10, 500, 200) };
            Notify(); Check(native.Positions.Count == 2, "Region capture on the active scaling display was excluded");
            Reset(); state = state with { CaptureArea = new(-1800, 10, 500, 200) };
            Notify(); NoPosition("Unrelated monitor capture region was raised");
            native.ScalingBounds = new(-1920, 0, 1920, 1080); Notify();
            Check(native.Positions.Count == 2, "Negative-coordinate scaling display was rejected");
            Reset(); state = state with { CaptureArea = null }; Notify(); NoPosition("Unconfigured region followed Magpie");
            state = state with { Mode = CaptureMode.Clipboard }; native.BodyBounds = new(-1800, 10, 800, 240); Notify();
            Check(native.Positions.Count == 2, "Clipboard subtitle on active scaling display was excluded");
            Reset(); native.BodyBounds = new(100, 100, 800, 240); Notify(); NoPosition("Clipboard subtitle on unrelated monitor was raised");

            state = state with { Mode = CaptureMode.Window, Target = game };
            order.HandleMessage(FakeWindows.Message, 1, 30); order.Dispose();
            NoPosition("Closed/disposed overlay still performed deferred native work");
            order.Dispose();
            Check(native.Stops == 1 && native.Receiver is null, "Overlay close leaked or double-disposed its notification receiver");
            Check(!order.HandleMessage(FakeWindows.Message, 1, 30), "Disposed watcher consumed notifications");
            order.Shown(); Check(queue.Count == 0, "Disposed watcher scheduled more work");
            var unavailable = new FakeWindows { MessageAvailable = false };
            using var disabled = new MagpieOverlayOrder(unavailable, () => state, queue.Enqueue);
            disabled.Attach(11); disabled.Shown();
            Check(!disabled.HandleMessage(0, 1, 30) && queue.Count == 0, "Registration failure captured unrelated messages or scheduled work");
            Check(JsonSerializer.Serialize(profile) == originalProfile && overlay.ClickThrough,
                "Notification work changed profile or click-through preferences");
            Check(overlay.Topmost && overlay.Chrome.Topmost && !overlay.ShowActivated && !overlay.Chrome.ShowActivated,
                "Notification work changed normal overlay topmost/activation properties");
        }
        finally { overlay.Close(); }
    }

    private sealed class FakeWindows : IMagpieOverlayWindows
    {
        internal const int Message = 0xC123;
        internal bool Present = true, MessageAvailable = true, ValidTarget = true, PositionFails;
        internal int Listens, Stops;
        internal Action<int, nint, nint>? Receiver;
        internal nint Source = 10, Foreground = 10, LastCandidate;
        internal PixelRect ScalingBounds = new(0, 0, 1920, 1080), BodyBounds = new(100, 100, 800, 240);
        internal List<nint> Order = [30, 12, 11];
        internal readonly HashSet<nint> Hidden = [];
        internal readonly List<(nint Window, nint After, uint Flags)> Positions = [];
        public int Listen(Action<int, nint, nint> received) { Listens++; Receiver = MessageAvailable ? received : null; return MessageAvailable ? Message : 0; }
        public void StopListening() { Stops++; Receiver = null; }
        internal void Deliver(nint eventId, nint output) => Receiver?.Invoke(Message, eventId, output);
        public MagpiePresentation? Find(nint candidate) {
            LastCandidate = candidate;
            return Present && (candidate == 0 || candidate == 30) ? new(30, Source, Foreground, ScalingBounds) : null;
        }
        public bool MatchesTarget(WindowTarget target) => ValidTarget;
        public bool IsVisible(nint window) => !Hidden.Contains(window);
        public PixelRect? Bounds(nint window) => BodyBounds;
        public bool IsAbove(nint higher, nint lower) => Order.Contains(higher) && Order.Contains(lower) && Order.IndexOf(higher) < Order.IndexOf(lower);
        public bool Position(nint window, nint after, uint flags) {
            Positions.Add((window, after, flags));
            if (PositionFails) return false;
            Order.Remove(window); Order.Insert(0, window); return true;
        }
    }
}
