using System.ComponentModel;
using Watashi.Client.Services;

namespace Watashi.Client;

public partial class MainWindow
{
    private MaintenanceMonitorService? _maintenance;
    private Task _maintenanceReady = Task.CompletedTask;
    private readonly CancellationTokenSource _maintenanceLifetime = new();
    public MaintenanceMonitorService? Maintenance => _maintenance;

    public void AttachMaintenance(MaintenanceMonitorService monitor)
    {
        _maintenance = monitor;
        ConnectionStatusText.GetBindingExpression(System.Windows.Controls.TextBlock.TextProperty)?.UpdateTarget();
        monitor.PropertyChanged += OnMaintenanceChanged;
        Closed += OnMaintenanceClosed;
        _maintenanceReady = ApplyMaintenanceAsync();
    }

    private Task EnsureMaintenanceReadyAsync() => _maintenanceReady;

    private void OnMaintenanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_maintenanceLifetime.IsCancellationRequested) return;
        var previous = _maintenanceReady;
        _maintenanceReady = ApplyAfterAsync(previous);
    }

    private async Task ApplyAfterAsync(Task previous)
    {
        await previous;
        if (!_maintenanceLifetime.IsCancellationRequested) await ApplyMaintenanceAsync();
    }

    private async Task ApplyMaintenanceAsync()
    {
        if (_maintenance is null) return;
        var wasBlocked = _vm.RemoteOperationsBlocked;
        _vm.RemoteOperationsBlocked = _maintenance.IsBlocked;
        _vm.RemoteOperationBlockReason = _maintenance.IsBlocked ? _maintenance.Message : string.Empty;
        try
        {
            // An unknown/old server cannot release a persisted maintenance hold.
            if (_maintenance.IsBlocked)
                await _vm.TransferQueue.SetMaintenanceAsync(true, _maintenanceLifetime.Token);
            else if (_maintenance.IsVerified && _maintenance.Status is { IsBlocking: false })
            {
                await _vm.TransferQueue.SetMaintenanceAsync(false, _maintenanceLifetime.Token);
                if (wasBlocked && IsLoaded && !_maintenanceLifetime.IsCancellationRequested)
                {
                    await _vm.Remote.LoadHostsAndLocationsAsync();
                    await _vm.Remote.RefreshAsync();
                }
            }
        }
        catch (OperationCanceledException) when (_maintenanceLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _vm.RemoteOperationsBlocked = true;
            _vm.RemoteOperationBlockReason = "転送の待機状態を保存できません。保存先を確認してください。";
            _vm.StatusMessage = _vm.RemoteOperationBlockReason + " " + ex.Message;
        }
    }

    private void OnMaintenanceClosed(object? sender, EventArgs e)
    {
        _maintenanceLifetime.Cancel();
        if (_maintenance is not null) _maintenance.PropertyChanged -= OnMaintenanceChanged;
        Closed -= OnMaintenanceClosed;
    }
}
