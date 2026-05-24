using CommunityToolkit.Mvvm.ComponentModel;

namespace Watashi.Client.ViewModels.Admin;

public partial class AdminShellViewModel : ObservableObject
{
    public UserManagementViewModel Users { get; }
    public HostManagementViewModel Hosts { get; }
    public ShareManagementViewModel Shares { get; }
    public PermissionTemplateViewModel Templates { get; }
    public UserPermissionViewModel UserPermissions { get; }
    public PermissionBundleViewModel Bundles { get; }
    public DeviceManagementViewModel Devices { get; }
    public NodeManagementViewModel Nodes { get; }
    public AuditLogViewModel Logs { get; }
    public SystemSettingsViewModel Settings { get; }

    public AdminShellViewModel(
        UserManagementViewModel users, HostManagementViewModel hosts, ShareManagementViewModel shares,
        PermissionTemplateViewModel templates, UserPermissionViewModel perms, PermissionBundleViewModel bundles,
        DeviceManagementViewModel devices,
        NodeManagementViewModel nodes, AuditLogViewModel logs, SystemSettingsViewModel settings)
    {
        Users = users; Hosts = hosts; Shares = shares; Templates = templates;
        UserPermissions = perms; Bundles = bundles; Devices = devices; Nodes = nodes; Logs = logs; Settings = settings;
    }
}
