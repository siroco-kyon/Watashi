namespace Watashi.Shared.DTOs.Admin;

public class HostDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string HostAddress { get; set; } = string.Empty;
    public int Port { get; set; } = 445;
    public string? Description { get; set; }
    public string CredUsername { get; set; } = string.Empty;
    public int ExecutionNodeId { get; set; }
    public string? ExecutionNodeName { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateHostRequest
{
    public string Name { get; set; } = string.Empty;
    public string HostAddress { get; set; } = string.Empty;
    public int Port { get; set; } = 445;
    public string? Description { get; set; }
    public string CredUsername { get; set; } = string.Empty;
    public string CredPassword { get; set; } = string.Empty;
    public int ExecutionNodeId { get; set; }
}

public class UpdateHostRequest
{
    public string? Name { get; set; }
    public string? HostAddress { get; set; }
    public int? Port { get; set; }
    public string? Description { get; set; }
    public string? CredUsername { get; set; }
    public string? CredPassword { get; set; }
    public int? ExecutionNodeId { get; set; }
}

/// <summary>
/// フォーム入力値での接続テスト用。保存前/編集中の値をそのまま検証できる。
/// </summary>
public class TestHostConnectionRequest
{
    /// <summary>編集中の既存ホスト。共有名の借用と、パスワード空欄時の保存済みパスワード解決に使う。</summary>
    public int? HostId { get; set; }
    public string HostAddress { get; set; } = string.Empty;
    public int Port { get; set; } = 445;
    public string CredUsername { get; set; } = string.Empty;
    /// <summary>空欄なら (HostId 指定時) 保存済みパスワードを使用する。</summary>
    public string? CredPassword { get; set; }
    public int ExecutionNodeId { get; set; }
}
