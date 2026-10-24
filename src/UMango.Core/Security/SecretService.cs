// SPDX-License-Identifier: Apache-2.0
//
// 本文件基于 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/Utils/Secret/EncryptHelper.cs 逐字采用。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 PCL.Core.Utils.Secret → UMango.Core.Security；
//   2. **密钥保护改为平台抽象**（IKeyProtector）：PCL 的密钥靠 Windows DPAPI
//      保护（还有 CNG 版），只支持 Windows；UMango 要跨平台，
//      Windows 走 DPAPI、其它平台走文件系统权限。
//      PCL 原版还依赖 NuGet 包 PlainToolkit.CngProtectedData，UMango 不引入它 ——
//      那个包只提供 CNG DPAPI，比 ProtectedData 复杂度和依赖都高，
//      而 PCL 自己都保留了 v1（ProtectedData）路径并做惰性迁移，说明两者兼容。
//   3. **不引入 PCL 的 Identify/IdentifyOld 机器识别码**：那套是为了给
//      「同一台机器」生成稳定标识，用于旧版 AES-CBC 密钥派生。
//      UMango 没有历史数据要兼容，不需要这一层。
//   4. 加密提供者的版本号语义保留（0 = 纯软件 ChaCha20、1 = ChaCha20-Poly1305、
//      2 = AES-GCM），密文容器格式也保留（magic + version + length）。
//   5. 密钥文件的版本号与「数据算法版本」语义无关（沿用 PCL 的注释澄清）。

using System.Security.Cryptography;
using UMango.Core.Logging;
using UMango.Core.Security.Platform;

namespace UMango.Core.Security;

/// <summary>
/// 机密数据的加密存储。
///
/// 用法：
/// <code>
/// var secrets = new SecretService(keyFilePath);
/// var encrypted = secrets.Encrypt("token");
/// var plain = secrets.Decrypt(encrypted);
/// </code>
///
/// **密钥生命周期**：
/// ① 首次使用 → 生成一把 32 字节随机主密钥，用平台保护器保护后落盘；
/// ② 之后每次使用 → 读回密钥文件、解保护、缓存；
/// ③ 密钥文件损坏 → 改名留存（.broken-时间戳），重建一把新密钥，
///    并明确告知「用旧密钥保护的数据已无法恢复」。
/// </summary>
public sealed class SecretService
{
    /// <summary>纯软件 ChaCha20（无认证码）。</summary>
    public const uint VersionChaCha20Software = 0;

    /// <summary>ChaCha20-Poly1305。</summary>
    public const uint VersionChaCha20Poly1305 = 1;

    /// <summary>AES-GCM。</summary>
    public const uint VersionAesGcm = 2;

    private static readonly byte[] _Key = "UMango Secret Encryption Key"u8.ToArray();

    private readonly string _keyFilePath;
    private readonly IKeyProtector _protector;
    private readonly object _keyLock = new();
    private byte[]? _cachedKey;

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="keyFilePath">密钥文件路径。</param>
    /// <param name="protector">平台保护器；缺省用当前平台的。</param>
    public SecretService(string keyFilePath, IKeyProtector? protector = null)
    {
        if (string.IsNullOrWhiteSpace(keyFilePath))
            throw new ArgumentException("密钥文件路径不能为空。", nameof(keyFilePath));

        _keyFilePath = keyFilePath;
        _protector = protector ?? KeyProtectorFactory.Default;
    }

    /// <summary>当前平台选用的加密提供者与版本。</summary>
    public static (IEncryptionProvider Provider, uint Version) DefaultProvider => _DefaultProvider.Value;

    private static readonly Lazy<(IEncryptionProvider Provider, uint Version)> _DefaultProvider = new(_SelectBestEncryption);

    private static (IEncryptionProvider Provider, uint Version) _SelectBestEncryption()
    {
        var aesHardwareSupport = System.Runtime.Intrinsics.X86.Aes.IsSupported ||
                                 System.Runtime.Intrinsics.Arm.Aes.IsSupported;
        if (aesHardwareSupport && AesGcmProvider.Instance.IsSupported)
            return (AesGcmProvider.Instance, VersionAesGcm);
        if (ChaCha20Poly1305Provider.Instance.IsSupported)
            return (ChaCha20Poly1305Provider.Instance, VersionChaCha20Poly1305);

        // 兜底：纯软件 ChaCha20 **没有认证码**。
        // 它能保密但不能发现密文被篡改，所以新数据不应该走这条路径；
        // 这里保留它只是为了「万一运行时不支持前两者」时不至于完全无法使用。
        return (ChaCha20SoftwareProvider.Instance, VersionChaCha20Software);
    }

    /// <summary>加密一段文本。</summary>
    public string Encrypt(string? data)
    {
        if (string.IsNullOrEmpty(data)) return string.Empty;

        var rawData = System.Text.Encoding.UTF8.GetBytes(data);
        var (provider, version) = DefaultProvider;

        return Convert.ToBase64String(new EncryptionData(version, provider.Encrypt(rawData, EncryptionKey)).ToBytes());
    }

    /// <summary>解密一段文本。</summary>
    public string Decrypt(string? data)
    {
        if (string.IsNullOrEmpty(data)) return string.Empty;

        var rawData = Convert.FromBase64String(data);
        Exception? decryptError;

        if (EncryptionData.IsValid(rawData))
        {
            try
            {
                var encryptionData = EncryptionData.FromBytes(rawData);
                IEncryptionProvider provider = encryptionData.Version switch
                {
                    VersionChaCha20Software => ChaCha20SoftwareProvider.Instance,
                    VersionChaCha20Poly1305 => ChaCha20Poly1305Provider.Instance,
                    VersionAesGcm => AesGcmProvider.Instance,
                    _ => throw new NotSupportedException("Unsupported encryption version")
                };
                var decryptedData = provider.Decrypt(encryptionData.Data, EncryptionKey);
                return System.Text.Encoding.UTF8.GetString(decryptedData);
            }
            catch (Exception ex)
            {
                decryptError = ex;
            }
        }
        else
        {
            // 不是本格式的密文 —— 多半是别的工具写的，或者是明文。
            decryptError = new FormatException("数据不是受支持的加密格式。");
        }

        throw new InvalidOperationException("无法解密数据，数据可能已损坏。", decryptError);
    }

    /// <summary>
    /// 用户密钥。只在成功获取后缓存 —— 失败不会被记住，下次访问会重新尝试。
    /// </summary>
    /// <remarks>
    /// 密钥文件的 version（1 = 文件权限、2 = DPAPI）与
    /// <see cref="DefaultProvider"/> 的数据算法 version（0/1/2）语义无关。
    /// </remarks>
    private byte[] EncryptionKey
    {
        get
        {
            lock (_keyLock)
                return _cachedKey ??= _LoadOrCreateKey();
        }
    }

    /// <summary>清掉密钥缓存（下次访问会重新读盘）。</summary>
    public void ResetKeyCache()
    {
        lock (_keyLock) _cachedKey = null;
    }

    private byte[] _LoadOrCreateKey()
    {
        var keyFile = _keyFilePath;
        try
        {
            if (File.Exists(keyFile)) return _ReadKey(keyFile);
        }
        catch (Exception ex) when (_IsKeyUnusable(ex))
        {
            Log.Error("Encryption", ex,
                $"用户密钥无法解密，将重建（HRESULT=0x{ex.HResult:X8}，file={keyFile}）");
        }

        return _CreateKey(keyFile);
    }

    /// <summary>读取密钥。内容不可用时抛出。</summary>
    private byte[] _ReadKey(string keyFile)
    {
        var data = EncryptionData.FromBytes(File.ReadAllBytes(keyFile));

        if (data.Version != _protector.ProtectorVersion)
            throw new NotSupportedException($"Unsupported key version: {data.Version}");

        return _protector.Unprotect(data.Data);
    }

    /// <summary>生成并落盘一把新密钥。</summary>
    private byte[] _CreateKey(string keyFile)
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);

        var storeData = new EncryptionData(_protector.ProtectorVersion, _protector.Protect(key)).ToBytes();

        // 已确认能重建，才动原文件
        _QuarantineBrokenKeyFile(keyFile);

        if (!_WriteKeyFile(keyFile, storeData))
            Log.Error("Encryption", $"用户密钥写入失败，本次运行仅使用内存密钥：{keyFile}");
        else
            Log.Warn("Encryption", "用户密钥已重建。用旧密钥保护的数据无法恢复，需要重新登录。");

        return key;
    }

    /// <summary>原子写入密钥文件（同目录临时文件 + 覆盖式移动）。</summary>
    private static bool _WriteKeyFile(string keyFile, byte[] storeData)
    {
        try
        {
            AtomicFile.Write(keyFile, storeData);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Encryption", ex, $"写入用户密钥失败：{keyFile}");
            return false;
        }
    }

    /// <summary>把无法解密的密钥文件改名留存，便于取证。</summary>
    private static void _QuarantineBrokenKeyFile(string keyFile)
    {
        try
        {
            if (File.Exists(keyFile))
                File.Move(keyFile, $"{keyFile}.broken-{DateTime.Now:yyyyMMddHHmmss}", true);
        }
        catch (Exception ex)
        {
            Log.Warn("Encryption", $"留存无法解密的用户密钥失败：{keyFile}（{ex.Message}）");
        }
    }

    /// <summary>只有「内容确实不可用」才重建；I/O 类异常视为瞬时故障。</summary>
    private static bool _IsKeyUnusable(Exception ex) =>
        ex is CryptographicException or NotSupportedException or ArgumentException
            or FormatException or System.ComponentModel.Win32Exception;
}
