using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace GroupDashboard.Api.Auth;

public sealed class TelegramInitDataValidator(IConfiguration configuration)
{
    private readonly string? _botToken = configuration["Telegram:BotToken"];
    private readonly int _maxAgeMinutes = configuration.GetValue("MiniApp:InitDataMaxAgeMinutes", 1440);

    public bool TryGetUserId(string? initData, out long userId)
    {
        userId = 0;
        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(initData))
            return false;

        try
        {
            var fields = QueryHelpers.ParseQuery(initData);
            if (!fields.TryGetValue("hash", out var hashValues))
                return false;

            var receivedHash = hashValues.ToString();
            if (receivedHash.Length != 64)
                return false;

            // Telegram: secret_key = HMAC_SHA256(key="WebAppData", data=bot_token).
            var secretKey = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes("WebAppData"),
                Encoding.UTF8.GetBytes(_botToken));

            var dataCheckString = string.Join("\n", fields
                .Where(pair => pair.Key != "hash")
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));

            var calculatedHash = HMACSHA256.HashData(
                secretKey,
                Encoding.UTF8.GetBytes(dataCheckString));

            byte[] providedHash;
            try
            {
                providedHash = Convert.FromHexString(receivedHash);
            }
            catch (FormatException)
            {
                return false;
            }

            if (!CryptographicOperations.FixedTimeEquals(calculatedHash, providedHash))
                return false;

            if (!fields.TryGetValue("auth_date", out var authDateValues) ||
                !long.TryParse(authDateValues.ToString(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var authDate))
                return false;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (authDate > now + 60 || now - authDate > TimeSpan.FromMinutes(_maxAgeMinutes).TotalSeconds)
                return false;

            if (!fields.TryGetValue("user", out var userValues))
                return false;

            using var userJson = JsonDocument.Parse(userValues.ToString());
            if (!userJson.RootElement.TryGetProperty("id", out var idElement) ||
                !idElement.TryGetInt64(out userId) || userId <= 0)
            {
                userId = 0;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException)
        {
            userId = 0;
            return false;
        }
    }
}
