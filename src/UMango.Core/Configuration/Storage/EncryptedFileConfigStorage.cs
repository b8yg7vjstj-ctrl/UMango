using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using UMango.Core.Utils;

namespace UMango.Core.Configuration.Storage;

/// <summary>
/// 加密存取仓库：套在另一个 <see cref="ConfigStorage"/> 外面，把值加密后再存。
///
/// 取自 PCL-CE 的 <c>PCL.Core/App/Configuration/Storage/EncryptedFileConfigStorage.cs</c>（Apache-2.0）。
///
/// **与 PCL 的实质差异**：PCL 的解密用 <c>EncryptHelper.SecretDecrypt</c>（内部细节，
/// 依赖它自己的密钥派生），且**解密失败时静默返回 false**（配置项读不到 → 当成没设过）。
/// UMango 改成用 <see cref="UMango.Core.Security.SecretService"/> 提供的密钥服务，
/// 并且解密失败时**仍然返回 false 但触发 <see cref="DecryptFailed"/>** ——
/// 静默吞掉一次解密失败，用户只会看到"我的令牌怎么没了"。
/// </summary>
public sealed class EncryptedFileConfigStorage : ConfigStorage
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonCompat.SerializerOptions)
    {
        WriteIndented = false,
        AllowOutOfOrderMetadataProperties = true,
    };

    private readonly ConfigStorage _source;
    private readonly ISecretProtector _protector;

    /// <summary>构造。</summary>
    /// <param name="source">底层存储。</param>
    /// <param name="protector">密钥保护器。</param>
    public EncryptedFileConfigStorage(ConfigStorage source, ISecretProtector protector)
    {
        _source = source;
        _protector = protector;
    }

    /// <summary>底层存储。</summary>
    public ConfigStorage Source => _source;

    /// <summary>解密失败时触发（参数为键名与异常）。</summary>
    public event Action<string, Exception>? DecryptFailed;

    /// <inheritdoc />
    protected override bool OnAccess<TKey, TValue>(
        StorageAction action,
        ref TKey key,
        [NotNullWhen(true)] ref TValue value,
        object? argument)
    {
        switch (action)
        {
            case StorageAction.Set:
            {
                var type = typeof(TValue);
                var text = type == typeof(string)
                    ? (value as string ?? string.Empty)
                    : JsonSerializer.Serialize(value, SerializerOptions);
                var encrypted = _protector.Protect(text);
                return _source.Access(StorageAction.Set, ref key, ref encrypted, argument);
            }

            case StorageAction.Get:
            {
                string? raw = null;
                if (!_source.Access(StorageAction.Get, ref key, ref raw, argument)) return false;
                if (raw is null) return false;

                string decrypted;
                try
                {
                    decrypted = _protector.Unprotect(raw);
                }
                catch (Exception ex)
                {
                    var keyName = key is string s ? s : key?.ToString() ?? string.Empty;
                    DecryptFailed?.Invoke(keyName, ex);
                    return false;
                }

                var type = typeof(TValue);
                if (type == typeof(bool))
                {
                    value = (TValue)(object)(decrypted.ToLowerInvariant() is "true" or "1");
                }
                else if (type == typeof(string))
                {
                    value = (TValue)(object)decrypted;
                }
                else
                {
                    var deserialized = JsonSerializer.Deserialize<TValue>(decrypted, SerializerOptions);
                    if (deserialized is null) throw new InvalidDataException("Decryption produced a null reference");
                    value = deserialized;
                }
                return true;
            }

            default:
                return _source.Access(action, ref key, ref value, argument);
        }
    }

    /// <inheritdoc />
    public override void Stop() => _source.Stop();
}

/// <summary>
/// 密钥保护器：把明文变成可安全落盘的密文，并还原。
///
/// UMango 已有 <c>UMango.Core.Security</c> 一族（AES-GCM / ChaCha20-Poly1305 + 平台密钥保护器），
/// 这里只定义配置层需要的最小契约，避免配置层依赖具体算法。
/// </summary>
public interface ISecretProtector
{
    /// <summary>加密（返回可安全落盘的密文文本）。</summary>
    string Protect(string plaintext);

    /// <summary>解密。</summary>
    string Unprotect(string ciphertext);
}
