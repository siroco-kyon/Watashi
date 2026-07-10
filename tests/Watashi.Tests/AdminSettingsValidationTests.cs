using FluentAssertions;
using Watashi.Server.Endpoints;
using Watashi.Shared.Constants;

namespace Watashi.Tests;

public class AdminSettingsValidationTests
{
    [Theory]
    [InlineData(SettingKeys.PasswordExpiryDays, "1")]
    [InlineData(SettingKeys.PasswordWarningDays, "0")]
    [InlineData(SettingKeys.AgentMaxConcurrency, "100000")]
    [InlineData(SettingKeys.SessionIdleMinutes, "30")]
    [InlineData(SettingKeys.AuditLogRetentionDays, "0")]
    [InlineData(SettingKeys.MaxFailedLoginAttempts, "15")]
    public void Known_setting_with_valid_integer_is_accepted(string key, string value)
    {
        AdminSettingsEndpoints.ValidateSetting(key, value).Should().BeNull();
    }

    [Theory]
    [InlineData("UnknownSetting", "1")]
    [InlineData(SettingKeys.PasswordExpiryDays, "0")]
    [InlineData(SettingKeys.PasswordWarningDays, "-1")]
    [InlineData(SettingKeys.AgentMaxConcurrency, "100001")]
    [InlineData(SettingKeys.SessionIdleMinutes, "not-a-number")]
    [InlineData(SettingKeys.AuditLogRetentionDays, "365001")]
    [InlineData(SettingKeys.MaxFailedLoginAttempts, "0")]
    public void Unknown_or_out_of_range_setting_is_rejected(string key, string value)
    {
        AdminSettingsEndpoints.ValidateSetting(key, value).Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100000)]
    public void Node_concurrency_accepts_supported_range(int value)
    {
        AdminNodeEndpoints.ValidateMaxConcurrency(value).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100001)]
    public void Node_concurrency_rejects_unsafe_range(int value)
    {
        AdminNodeEndpoints.ValidateMaxConcurrency(value).Should().NotBeNull();
    }
}
