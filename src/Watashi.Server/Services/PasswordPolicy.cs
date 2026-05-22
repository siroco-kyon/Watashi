namespace Watashi.Server.Services;

public static class PasswordPolicy
{
    public const int MinLength = 12;

    public static (bool ok, string? error) Validate(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return (false, $"パスワードは {MinLength} 文字以上である必要があります。");

        bool hasUpper = false, hasLower = false, hasDigit = false, hasSymbol = false;
        foreach (var c in password)
        {
            if (char.IsUpper(c)) hasUpper = true;
            else if (char.IsLower(c)) hasLower = true;
            else if (char.IsDigit(c)) hasDigit = true;
            else hasSymbol = true;
        }

        if (!hasUpper || !hasLower || !hasDigit || !hasSymbol)
            return (false, "パスワードには英大文字・英小文字・数字・記号をそれぞれ 1 文字以上含める必要があります。");

        return (true, null);
    }
}
