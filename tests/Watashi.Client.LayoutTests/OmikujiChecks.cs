using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Interop;
using Watashi.Client.Services;
using Watashi.Client.Views;

internal static partial class Program
{
    private static void CheckOmikuji(string? output, string theme)
    {
        var trigger = new OmikujiTrigger();
        for (var i = 0; i < 9; i++) Require(!trigger.Click(i * 200), "Omikuji opened before ten clicks");
        Require(trigger.Click(1800), "Ten clicks did not open omikuji");
        Require(!trigger.Click(1900), "Omikuji counter did not reset after opening");
        for (var i = 0; i < 8; i++) Require(!trigger.Click(2000 + i * 100), "Unexpected opening");
        Require(!trigger.Click(5700), "Three-second gap must reset clicks");
        for (var i = 1; i < 9; i++) Require(!trigger.Click(5700 + i * 2999), "Slow clicks opened early");
        Require(trigger.Click(5700 + 9 * 2999), "Clicks under three seconds should count");

        foreach (var size in new[] { new Size(320, 340), new Size(240, 180) })
        {
            var window = new OmikujiWindow();
            var root = (FrameworkElement)window.Content;
            window.Content = null;
            var canvas = new Border { Background = new SolidColorBrush((Color)Application.Current.Resources["BgColor"]), Child = root };
            Layout(canvas, size);
            var fortune = (TextBlock)window.FindName("FortuneText");
            var message = (TextBlock)window.FindName("MessageText");
            var draw = Descendants<Button>(root).Single(b => b.Content.ToString()!.StartsWith("もう一度"));
            for (var i = 0; i < 30; i++)
            {
                draw.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!string.IsNullOrWhiteSpace(fortune.Text) && !string.IsNullOrWhiteSpace(message.Text), "Empty fortune");
            }
            // Exercise the widest label and a long message at the smallest supported content size.
            fortune.Text = "寄り道吉";
            message.Text = "ここを見つけたあなたに、とっておきの吉。";
            Layout(canvas, size);
            Require(draw.ActualWidth > 0 && draw.TranslatePoint(new Point(0, draw.ActualHeight), canvas).Y <= size.Height,
                "Draw button is outside the window");
            var scroll = Descendants<ScrollViewer>(root).First();
            scroll.ScrollToEnd();
            Layout(canvas, size);
            Require(scroll.ScrollableHeight == 0 || scroll.VerticalOffset > 0, "Fortune cannot be scrolled");
            Save(canvas, output, $"omikuji-{theme}-{size.Width}.png");
            using var source = new HwndSource(new HwndSourceParameters("Omikuji keyboard check")
            {
                ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0,
            });
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            });
            Require(closed, "Esc did not close omikuji");
        }
    }
}
