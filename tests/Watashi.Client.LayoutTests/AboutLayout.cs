using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Watashi.Client.Services;
using Watashi.Client.Views;

internal static partial class Program
{
    private static void CheckAbout(string? output, string theme)
    {
        foreach (var size in new[] { new Size(450, 510), new Size(300, 260) })
        {
            var window = new AboutWindow();
            var root = (FrameworkElement)window.Content;
            window.Content = null;
            var canvas = new Border { Background = (Brush)Application.Current.Resources["BgBrush"], Child = root };
            Layout(canvas, size);
            Require(((TextBox)window.FindName("VersionText")).Text == AppVersion.Display, "About version is not current");
            var greeting = (TextBlock)window.FindName("GreetingText");
            var before = greeting.Text;
            ((Button)window.FindName("GreetingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(greeting.Text != before, "Greeting did not change");
            var scroll = Descendants<ScrollViewer>(root).First();
            scroll.ScrollToEnd();
            Layout(canvas, size);
            if (size.Height < 300) Require(scroll.VerticalOffset > 0, "Small About cannot scroll");
            var position = greeting.TransformToAncestor(scroll).Transform(new Point());
            Require(position.Y >= 0 && position.Y + greeting.ActualHeight <= scroll.ActualHeight + 1, "Greeting is clipped");
            Save(canvas, output, $"about-{theme}-{size.Width}.png");
            window.Close();
        }
    }
}
