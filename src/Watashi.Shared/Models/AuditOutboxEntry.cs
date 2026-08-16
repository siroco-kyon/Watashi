namespace Watashi.Shared.Models;

/// <summary>本処理の完了後に監査テーブルだけが一時的に書けなかったイベント。</summary>
public sealed class AuditOutboxEntry
{
    public Guid EventId { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
}
