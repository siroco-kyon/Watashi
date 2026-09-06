using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Watashi.Client.Services;
using Watashi.Client.ViewModels;
using Watashi.Client.Views;

internal static partial class Program
{
    private static void CheckTransfers(Size size, string? output, string theme)
    {
        var vm = new TransferQueueViewModel(new TransferQueueService(new TransferQueueStore(), null!));
        var jobs = Enumerable.Range(0, 2000).Select(i => new TransferJobRecord
        {
            Id = $"job-{i}", LocalPath = $"C:\\資料\\転送ファイル{i:D4}.zip", RemotePath = "/共有/資料.zip",
            State = i % 2 == 0 ? TransferJobStates.Failed : TransferJobStates.Completed,
            LastError = string.Join("\n", Enumerable.Repeat("接続先を確認してください。長いエラーの説明です。", 30)),
        }).ToArray();
        typeof(TransferQueueViewModel).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [jobs]);
        Require(vm.FilterOptions.Single(o => o.Key == "要対応").Count == 1000, "Attention filter count");
        var window = new TransferCenterWindow(vm);
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        root.DataContext = vm;
        var canvas = new Border { Background = (Brush)Application.Current.Resources["BgBrush"], Child = root };
        Layout(canvas, size);
        var list = (DataGrid)window.FindName("JobList");
        Require(list.ActualHeight >= 160, $"Transfer list too short: {list.ActualHeight} at {size}");
        Require(Descendants<DataGridRow>(list).Count() is > 2 and < 100, "Transfer virtualization");
        list.SelectedItem = vm.Jobs[^1];
        list.ScrollIntoView(vm.Jobs[^1]);
        Descendants<ScrollViewer>(list).First().ScrollToEnd();
        Layout(canvas, size);
        Require(list.ItemContainerGenerator.ContainerFromItem(vm.Jobs[^1]) is not null, "Last transfer unreachable");
        vm.SelectedJob = vm.Jobs[0];
        ((Button)window.FindName("OpenDetailsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(canvas, size);
        var detail = (Border)window.FindName("DetailPane");
        Require(detail.Visibility == Visibility.Visible && detail.ActualHeight >= 160, "Transfer detail inaccessible");
        var scroll = Descendants<ScrollViewer>(detail).First();
        Require(scroll.ScrollableHeight > 0, "Long error should scroll");
        scroll.ScrollToEnd();
        Layout(canvas, size);
        Require(scroll.VerticalOffset > 0, "Detail cannot scroll to bottom");
        Save(canvas, output, $"transfer-detail-{theme}-{size.Width}.png");
        ((Button)window.FindName("CloseDetailsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(canvas, size);
        Require(list.Visibility == Visibility.Visible && ReferenceEquals(vm.SelectedJob, vm.Jobs[0]), "Back loses selection");
        vm.SelectedFilter = "要対応";
        Require(vm.VisibleJobs.Cast<object>().Count() == 1000, "Attention filter mismatch");
        Descendants<ScrollViewer>(list).First().ScrollToTop();
        ((ScrollViewer)window.FindName("PageScroll")).ScrollToTop();
        Layout(canvas, size);
        Require(Descendants<DataGridRow>(list).Any(), "Filtered rows must remain visible");
        Save(canvas, output, $"transfer-list-{theme}-{size.Width}.png");
        window.Close();
    }
}
