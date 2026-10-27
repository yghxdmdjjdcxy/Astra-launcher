using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace AstraLauncher.Authentication;

/// <summary>
/// Protects refresh tokens with Windows DPAPI (<see cref="DataProtectionScope.CurrentUser"/>) and returns the
/// ciphertext as Base64 so it can be stored in JSON. The plaintext never leaves process memory.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenProtector : ITokenProtector
{
    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("DPAPI 令牌保护仅在 Windows 上受支持。");
        }

        try
        {
            byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            byte[] protectedBytes = ProtectedData.Protect(
                plaintextBytes,
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException(
                "无法使用 DPAPI 加密令牌，请确认当前 Windows 用户的加密配置可用。",
                exception);
        }
    }

    /// <inheritdoc />
    public string Unprotect(string protectedText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedText);
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("DPAPI 令牌保护仅在 Windows 上受支持。");
        }

        try
        {
            byte[] protectedBytes = Convert.FromBase64String(protectedText);
            byte[] plaintextBytes = ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "无法解密令牌：保存的令牌不是有效的 Base64 数据。",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "无法解密令牌：保存的令牌数据格式无效。",
                exception);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException(
                "无法解密令牌：数据已损坏或不属于当前 Windows 用户。",
                exception);
        }
    }
}
