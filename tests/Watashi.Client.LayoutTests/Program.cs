using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Watashi.Client.Converters;
using Watashi.Client.ViewModels.Admin;
using Watashi.Client.Views.Admin;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.DTOs.Files;

// STA/WPF layout smoke tests. No windows are shown, and no production service is contacted.
internal static partial class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var dictionary in new[] { "Colors", "Icons", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/Watashi.Client;component/Themes/{dictionary}.xaml", UriKind.Relative),
            });
        app.Resources["BoolToVis"] = new BoolToVisibilityConverter();
        app.Resources["BoolToVisInverted"] = new BoolToVisibilityConverter { Invert = true };
        app.Resources["Utc"] = new UtcToLocalConverter();
        app.Resources["SizeFmt"] = new SizeFormatConverter();
        app.Resources["NotBool"] = new NotBoolConverter();
        app.Resources["StatusBrush"] = new StatusMessageToBrushConverter();
        app.Resources["DiagnosticStatusLabel"] = new DiagnosticStatusToLabelConverter();
        var output = args.FirstOrDefault();
        if (output is not null) Directory.CreateDirectory(output);

        foreach (var theme in new[] { "Light", "Dark" })
        {
            var palette = new ResourceDictionary
            {
                Source = new Uri($"/Watashi.Client;component/Themes/Colors.{theme}.xaml", UriKind.Relative),
            };
            foreach (var key in palette.Keys) app.Resources[key] = palette[key];
            CheckAbout(output, theme);
            CheckTransfers(new Size(1200, 900), output, theme);
            // Client-area DIPs, including the reduced space at 125/150% on a 1366x768 display.
            foreach (var size in new[] { new Size(1200, 720), new Size(1060, 550), new Size(890, 450), new Size(720, 340) })
            {
                CheckAdmin(size, output, theme);
                CheckTransfers(size, output, theme);
            }
        }
        CheckExpandedBrowser(output);
        Console.WriteLine("PASS: admin and transfer layout, scrolling, virtualization, draft retention and expanded browser.");
    }

    private static void CheckAdmin(Size size, string? output, string theme)
    {
        // The shell's Loaded handler is never run: only its content is laid out offscreen.
        var window = new AdminWindow(null!);
        var tabs = (TabControl)window.FindName("AdminTabs");
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        var canvas = new Border { Background = (Brush)Application.Current.Resources["BgBrush"], Child = root };

        var permissions = new UserPermissionViewModel(null!);
        // SafeAsync handles the unavailable API synchronously; populate the offline fixture afterwards.
        permissions.SelectedUser = new UserDto { Id = 1, Username = "test-admin", DisplayName = "検証ユーザー" };
        permissions.StatusMessage = "";
        for (var i = 0; i < 2000; i++)
            permissions.Items.Add(new UserPermissionDto
            {
                Id = i + 1, HostName = "ファイルサーバー", ShareName = "開発部", AllowedPath = $"/資料/フォルダー{i:D4}", TemplateName = "読み取り",
            });
        permissions.UserSummaries.Add(new UserPermissionSummary(permissions.SelectedUser!, 2000));
        for (var i = 0; i < 30; i++) permissions.BrowseEntries.Add(new FileEntry { Name = $"フォルダー{i:D4}", Type = FileEntryTypes.Directory });
        var permissionView = (UserPermissionView)((TabItem)tabs.Items[4]).Content;
        permissionView.DataContext = permissions;
        tabs.SelectedIndex = 4;
        Layout(canvas, size);
        var list = Descendants<ListView>(permissionView).Single(v => ReferenceEquals(v.ItemsSource, permissions.ItemsView));
        Require(list.ActualHeight >= 240, $"Permission list height {list.ActualHeight} at {size}");
        Require(Descendants<ListViewItem>(list).Count() is > 5 and < 100, "Permission list lost virtualization");
        list.ScrollIntoView(permissions.Items[^1]);
        Layout(canvas, size);
        Require(list.ItemContainerGenerator.ContainerFromItem(permissions.Items[^1]) is not null, "Last permission row is unreachable");
        list.ScrollIntoView(permissions.Items[0]);
        Layout(canvas, size);
        CheckPageScroll(root, size);
        Save(canvas, output, $"permissions-{theme}-{size.Width}.png");

        permissions.Selected = permissions.Items[0];
        var editorTabs = Descendants<TabControl>(permissionView).Single();
        editorTabs.SelectedIndex = 1;
        Layout(canvas, size);
        var draft = Descendants<TextBox>(permissionView).Single(b =>
            b.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "NewAllowedPath");
        draft.Text = "/未保存の入力";
        draft.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        var browser = Descendants<ListBox>(permissionView).Single(v => ReferenceEquals(v.ItemsSource, permissions.BrowseEntries));
        Require(browser.ActualHeight >= 190, "Inline path browser is too short");
        CheckLastActionReachable(permissionView, canvas, size, "＋ この期限付き権限を追加");
        editorTabs.SelectedIndex = 0;
        Layout(canvas, size);
        editorTabs.SelectedIndex = 1;
        Layout(canvas, size);
        Require(permissions.NewAllowedPath == "/未保存の入力" && permissions.Selected == permissions.Items[0], "Tab switch discarded draft/selection");
        Save(canvas, output, $"permission-editor-{theme}-{size.Width}.png");

        var bundles = new PermissionBundleViewModel(null!);
        var bundle = new PermissionBundleDto { Id = 1, Name = "開発部の基本権限", Description = "一覧と追加フォームの表示領域を確認" };
        bundle.Entries.AddRange(Enumerable.Range(0, 2000).Select(i => new PermissionBundleEntryDto
        {
            Id = i + 1, HostName = "ファイルサーバー", ShareName = "開発部", AllowedPath = $"/資料/フォルダー{i:D4}", TemplateName = "読み取り",
        }));
        bundles.Items.Add(bundle);
        bundles.Selected = bundle;
        var bundleView = (PermissionBundleView)((TabItem)tabs.Items[5]).Content;
        bundleView.DataContext = bundles;
        tabs.SelectedIndex = 5;
        Layout(canvas, size);
        var entryList = Descendants<ListView>(bundleView).Single();
        Require(entryList.ActualHeight >= 240, $"Bundle list height {entryList.ActualHeight} at {size}");
        Require(Descendants<ListViewItem>(entryList).Count() is > 5 and < 100, "Bundle list lost virtualization");
        Save(canvas, output, $"bundles-{theme}-{size.Width}.png");
        Descendants<TabControl>(bundleView).Single().SelectedIndex = 1;
        Layout(canvas, size);
        CheckLastActionReachable(bundleView, canvas, size, "＋ 行を追加 (保存はセット側で確定)");

        // Includes forms taller than the 600-DIP workspace, not just an empty listing.
        foreach (var index in new[] { 0, 1, 2, 3, 7, 10 })
        {
            tabs.SelectedIndex = index;
            Layout(canvas, size);
            var view = (FrameworkElement)((TabItem)tabs.Items[index]).Content;
            var form = Descendants<ScrollViewer>(view).First(s => s.Content is StackPanel);
            Require(form.ViewportHeight > 0 && double.IsFinite(form.ViewportHeight), "Unbounded form viewport");
            form.ScrollToBottom();
            Layout(canvas, size);
            Require(Math.Abs(form.VerticalOffset - form.ScrollableHeight) < 1, "Cannot reach form bottom");
            if (index == 2) Save(canvas, output, $"share-form-{theme}-{size.Width}.png");
        }
        Console.WriteLine($"PASS: {theme} {size.Width}x{size.Height}; permission rows area {list.ActualHeight:0}, bundle rows area {entryList.ActualHeight:0}");
    }

    private static void CheckPageScroll(FrameworkElement root, Size size)
    {
        var page = Descendants<ScrollViewer>(root).Single(s => s.Name == "PageScroll");
        Require(page.ViewportHeight > 0, "Admin content template has no viewport");
        if (size.Height < 600) Require(page.ScrollableHeight > 0, "Small screen cannot scroll the whole page");
        if (size.Width < 900) Require(page.ScrollableWidth > 0, "Small screen cannot reach the right edge");
        page.ScrollToBottom();
        page.ScrollToRightEnd();
        Layout(root, size);
        Require(Math.Abs(page.VerticalOffset - page.ScrollableHeight) < 1, "Cannot reach page bottom");
        page.ScrollToTop();
        page.ScrollToLeftEnd();
        Layout(root, size);
    }

    private static void CheckLastActionReachable(FrameworkElement view, FrameworkElement root, Size size, string caption)
    {
        var button = Descendants<Button>(view).Single(b => Equals(b.Content, caption));
        var scroll = Ancestor<ScrollViewer>(button);
        scroll.ScrollToBottom();
        Layout(root, size);
        var bounds = button.TransformToAncestor(scroll).TransformBounds(new Rect(button.RenderSize));
        Require(bounds.Top >= 0 && bounds.Bottom <= scroll.ActualHeight + 1, $"Unreachable action: {caption}");
    }

    private static void CheckExpandedBrowser(string? output)
    {
        var vm = new PermissionBundleViewModel(null!) { BrowsePath = "/資料" };
        for (var i = 0; i < 2000; i++) vm.BrowseEntries.Add(new FileEntry { Name = $"フォルダー{i:D4}", Type = FileEntryTypes.Directory });
        var window = new AdminPathBrowserWindow(vm, nameof(vm.EntryAllowedPath));
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        root.DataContext = vm;
        Layout(root, new Size(740, 590));
        var list = Descendants<ListBox>(root).Single();
        Require(list.ActualHeight >= 250, "Expanded path browser is too short");
        list.SelectedItem = vm.BrowseEntries[8];
        Layout(root, new Size(740, 590));
        Require(vm.EntryAllowedPath == "/資料/フォルダー0008", "Expanded browser did not update the original draft");
        Require(Descendants<ListBoxItem>(list).Count() is > 5 and < 100, "Expanded browser lost virtualization");
        var canvas = new Border { Background = (Brush)Application.Current.Resources["BgBrush"], Child = root };
        Layout(canvas, new Size(740, 590));
        Save(canvas, output, "expanded-browser.png");

        var permissionVm = new UserPermissionViewModel(null!) { BrowsePath = "/共有" };
        permissionVm.BrowseEntries.Add(new FileEntry { Name = "選択したフォルダー", Type = FileEntryTypes.Directory });
        var permissionWindow = new AdminPathBrowserWindow(permissionVm, nameof(permissionVm.NewAllowedPath));
        var permissionRoot = (FrameworkElement)permissionWindow.Content;
        permissionWindow.Content = null;
        permissionRoot.DataContext = permissionVm;
        var smallCanvas = new Border { Child = permissionRoot };
        Layout(smallCanvas, new Size(480, 320));
        Descendants<ListBox>(permissionRoot).Single().SelectedIndex = 0;
        Layout(smallCanvas, new Size(480, 320));
        Require(permissionVm.NewAllowedPath == "/共有/選択したフォルダー", "User permission browser did not update the draft");
        var close = Descendants<Button>(permissionRoot).Single(b => b.IsCancel);
        var bounds = close.TransformToAncestor(smallCanvas).TransformBounds(new Rect(close.RenderSize));
        Require(bounds.Bottom <= 320 && bounds.Right <= 480, "Small browser cannot reach Close");
    }

    private static T Ancestor<T>(DependencyObject node) where T : DependencyObject
        => VisualTreeHelper.GetParent(node) is T result ? result : Ancestor<T>(VisualTreeHelper.GetParent(node));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Layout(FrameworkElement root, Size size)
    {
        for (var i = 0; i < 4; i++)
        {
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
    }

    private static void Save(FrameworkElement root, string? output, string name)
    {
        if (output is null) return;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name));
        encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
