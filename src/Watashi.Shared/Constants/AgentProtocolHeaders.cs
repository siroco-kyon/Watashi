namespace Watashi.Shared.Constants;

/// <summary>Server と Agent の内部通信で使用する共通ヘッダー名。</summary>
public static class AgentProtocolHeaders
{
    /// <summary>
    /// 共通の共有秘密で認証された通信について、送信元が申告する Agent ID。
    /// 値は中央の ExecutionNode.Name と完全一致させる。
    /// </summary>
    public const string AgentId = "X-Watashi-Agent-Id";
}
