using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Watashi.Client.Controls;
using Watashi.Client.Services;
using Watashi.Client.ViewModels;

internal static partial class Program
{
    private static void CheckCompactTransfers(string? output, string theme)
    {
        var vm = new TransferQueueViewModel(new TransferQueueService(new TransferQueueStore(), null!));
        var jobs = new[]
        {
            new TransferJobRecord { Id = "upload", LocalPath = @"C:\資料\とても長い名前の売上資料.zip", RemotePath = "/共有/売上資料.zip", State = TransferJobStates.Running, TotalBytes = 200, BytesTransferred = 150 },
            new TransferJobRecord { Id = "download", Direction = TransferDirections.Download, RemotePath = "/共有/設計図.pdf", LocalPath = @"C:\資料\設計図.pdf", State = TransferJobStates.Running, TotalBytes = 40, BytesTransferred = 12 },
            new TransferJobRecord { Id = "queued", State = TransferJobStates.Queued },
            new TransferJobRecord { Id = "failed", State = TransferJobStates.Failed },
        };
        void Snapshot() => typeof(TransferQueueViewModel).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [jobs]);
        Snapshot();
        Require(vm.ActiveJobs.Count == 2 && vm.CompactSummary == "待機 1件 · 要対応 1件", "Compact transfer counts");
        vm.SelectedFilter = "要対応";
        Require(vm.ActiveJobs.Count == 2, "Center filter hides ongoing transfers");
        var strip = new TransferStatusStrip { DataContext = vm };
        var canvas = new Border { Background = (Brush)Application.Current.Resources["BgBrush"], Child = strip };
        foreach (var width in new[] { 720, 530, 400 })
        {
            Layout(canvas, new Size(width, 28));
            Require(strip.ActualHeight == 28, "Transfer strip consumes extra height");
            var bars = Descendants<ProgressBar>(strip).ToArray();
            Require(bars.Length == 2, "Both simultaneous transfers must be visible");
            foreach (var bar in bars)
            {
                var position = bar.TransformToAncestor(strip).Transform(new Point());
                Require(bar.ActualWidth >= 50 && position.X >= 0 && position.X + bar.ActualWidth <= width, "Progress bar is clipped");
            }
            Require(bars.Select(b => b.Value).Order().SequenceEqual(new double[] { 30, 75 }), "Wrong transfer percentages");
            Require(strip.IsCompact == (width < 650), "Responsive filename display");
            Save(canvas, output, $"compact-transfer-{theme}-{width}.png");
        }
        var first = vm.ActiveJobs.Single(job => job.Id == "upload");
        jobs[0].BytesTransferred = 180;
        Snapshot();
        Require(ReferenceEquals(first, vm.ActiveJobs.Single(job => job.Id == "upload")) && first.Percent == 90, "Progress update rebuilds active row or leaves stale progress");
        Layout(canvas, new Size(530, 28));
        Require(Descendants<ProgressBar>(strip).Any(bar => bar.Value == 90), "Rendered progress does not update");
        jobs[0].State = TransferJobStates.Canceling;
        Snapshot();
        Require(first.CompactProgress == "中止中", "Canceling looks like normal transfer");
        foreach (var job in jobs) job.State = TransferJobStates.Completed;
        Snapshot();
        Layout(canvas, new Size(530, 28));
        Require(!vm.HasActiveJobs && vm.CompactSummary == "転送なし", "Finished transfers remain active");
        var opened = false;
        strip.OpenRequested += (_, _) => opened = true;
        ((Button)strip.FindName("OpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(opened, "Transfer strip cannot open center");
    }
}
