namespace Watashi.Shared.DTOs.Admin;

public class UserDto
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public bool IsLocked { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime PasswordExpiresAt { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
}

public class UpdateUserRequest
{
    public bool? IsAdmin { get; set; }
}

public class ResetPasswordRequest
{
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>CSV インポートの動作モード。</summary>
public static class UserImportModes
{
    /// <summary>既存と同名のユーザーはスキップ。新規のみ追加。</summary>
    public const string AddOnly = "add-only";
    /// <summary>既存ユーザーも上書き (パスワード再設定 + IsAdmin 更新)。MustChangePassword=true にリセット。</summary>
    public const string Upsert = "upsert";
}

public class UserImportResultDto
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<UserImportRowError> Errors { get; set; } = new();
}

public class UserImportRowError
{
    public int LineNumber { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
