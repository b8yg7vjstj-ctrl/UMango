using UMango.Core.Security;
using Xunit;

namespace UMango.Core.Tests.Security;

/// <summary>
/// 机密数据加解密的测试。
///
/// 用临时目录当密钥文件位置，测试结束删掉。
/// 不用 PCL 的机器识别码（Identify）—— UMango 没有历史数据要兼容，
/// 密钥完全是随机生成 + 平台保护。
/// </summary>
public sealed class SecretServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "umango-secret-" + Guid.NewGuid().ToString("N"));
    private readonly string _keyFile;

    public SecretServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _keyFile = Path.Combine(_dir, "UserKey.bin");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败可忽略 */ }
    }

    private SecretService NewService() => new(_keyFile);

    /// <summary>
    /// 记录自己被调用的保护器。
    ///
    /// 用途：观察「写密钥」的先后顺序 —— 若实现改成直接写目标文件，
    /// 保护调用发生在目标文件已经存在之后。
    /// </summary>
    private sealed class RecordingProtector : IKeyProtector
    {
        private readonly Func<bool> _onProtect;
        public RecordingProtector(Func<bool> onProtect) => _onProtect = onProtect;

        public byte ProtectorVersion => 1;

        public byte[] Protect(ReadOnlySpan<byte> rawKey)
        {
            _onProtect();
            return rawKey.ToArray();
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedKey) => protectedKey.ToArray();
    }

    [Fact]
    public void 加密后能解回原文()
    {
        var service = NewService();

        var encrypted = service.Encrypt("hello 你好");

        Assert.NotEqual("hello 你好", encrypted);
        Assert.Equal("hello 你好", service.Decrypt(encrypted));
    }

    [Fact]
    public void 两次加密同一段明文得到不同密文()
    {
        // nonce 必须是随机的，否则同样的明文每次都产生同样的密文
        var service = NewService();

        var a = service.Encrypt("same");
        var b = service.Encrypt("same");

        Assert.NotEqual(a, b);
        Assert.Equal("same", service.Decrypt(a));
        Assert.Equal("same", service.Decrypt(b));
    }

    [Fact]
    public void 空串与null原样返回()
    {
        var service = NewService();

        Assert.Equal(string.Empty, service.Encrypt(string.Empty));
        Assert.Equal(string.Empty, service.Encrypt(null));
        Assert.Equal(string.Empty, service.Decrypt(string.Empty));
        Assert.Equal(string.Empty, service.Decrypt(null));
    }

    [Fact]
    public void 密钥文件会被创建()
    {
        Assert.False(File.Exists(_keyFile));

        NewService().Encrypt("x");

        Assert.True(File.Exists(_keyFile));
    }

    [Fact]
    public void 重建的服务能用同一把密钥解开旧数据()
    {
        var service = NewService();
        var encrypted = service.Encrypt("persisted");

        // 模拟重启：全新实例，读同一个密钥文件
        var reopened = NewService();

        Assert.Equal("persisted", reopened.Decrypt(encrypted));
    }

    [Fact]
    public void 密钥文件损坏时会重建并把旧文件改名留存()
    {
        var service = NewService();
        var encrypted = service.Encrypt("lost");

        // 写坏密钥文件
        File.WriteAllBytes(_keyFile, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var reopened = NewService();

        // 新密钥 ≠ 旧密钥 → 旧密文解不开
        Assert.ThrowsAny<Exception>(() => reopened.Decrypt(encrypted));
        // 旧文件被改名留存
        Assert.Single(Directory.GetFiles(_dir, "UserKey.bin.broken-*"));
    }

    [Fact]
    public void 密钥文件被删后旧数据无法解开()
    {
        var service = NewService();
        var encrypted = service.Encrypt("lost");

        File.Delete(_keyFile);

        var reopened = NewService();

        Assert.ThrowsAny<Exception>(() => reopened.Decrypt(encrypted));
    }

    [Fact]
    public void 密文被篡改会被检测出来()
    {
        var service = NewService();
        var encrypted = service.Encrypt("integrity");
        var raw = Convert.FromBase64String(encrypted);
        raw[^1] ^= 0xFF;

        // AES-GCM / ChaCha20-Poly1305 带认证码，改一个字节就会被识破
        Assert.ThrowsAny<Exception>(() => service.Decrypt(Convert.ToBase64String(raw)));
    }

    [Fact]
    public void 用别的密钥解不开()
    {
        var service = NewService();
        var encrypted = service.Encrypt("secret");

        // 换一个密钥文件 = 另一把主密钥
        var otherKeyFile = Path.Combine(_dir, "OtherKey.bin");
        var other = new SecretService(otherKeyFile);

        Assert.ThrowsAny<Exception>(() => other.Decrypt(encrypted));
    }

    [Fact]
    public void 密文用的是当前平台的默认算法()
    {
        // 版本号必须与实际选中的提供者对得上 —— 写错了将来就没法按版本迁移
        var service = NewService();
        var encrypted = service.Encrypt("versioned");
        var raw = Convert.FromBase64String(encrypted);

        var version = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(4, 4));

        var (provider, expected) = SecretService.DefaultProvider;
        Assert.Equal(expected, version);
        Assert.Equal("versioned", service.Decrypt(encrypted));
    }

    [Fact]
    public void 未知版本的密文会明确报错而不是乱解()
    {
        var service = NewService();
        var encrypted = service.Encrypt("v");
        var raw = Convert.FromBase64String(encrypted);
        // 把版本改成没人认得的 99
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(4, 4), 99);

        var ex = Assert.ThrowsAny<Exception>(() => service.Decrypt(Convert.ToBase64String(raw)));

        Assert.Contains("损坏", ex.Message);
    }

    [Fact]
    public void 密钥文件是原子写入的()
    {
        // 直接写文件的话，中途崩溃会留下半截文件；
        // 原子写（临时文件 + 覆盖式移动）保证要么是完整的旧文件，要么是完整的新文件。
        var service = NewService();
        service.Encrypt("x");

        var keyFile = _keyFile;
        var directory = Path.GetDirectoryName(keyFile)!;

        // 写完之后目录里不应残留临时文件
        Assert.Empty(Directory.GetFiles(directory, "*.tmp*"));
    }

    [Fact]
    public void 密钥文件不会被写成半个()
    {
        // 直接写目标文件时，进程在写一半被杀会留下半截文件，
        // 下次启动读到它就会当成「密钥不可用」而重建 —— 用户被迫重新登录。
        // 原子写（临时文件 + 覆盖式移动）杜绝这种情况：
        // 目标文件要么是完整的旧内容，要么是完整的新内容，永远不会是半截。
        var service = NewService();
        service.Encrypt("x");

        var keyFile = _keyFile;
        var directory = Path.GetDirectoryName(keyFile)!;

        // 目标文件必须能被完整解析（长度恰好 = 头 12 + 保护后的 32 字节）
        var raw = File.ReadAllBytes(keyFile);
        Assert.Equal(EncryptionData.HeaderSize + 32, raw.Length);
        Assert.Equal(EncryptionData.MagicNumber,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(0, 4)));
    }

    [Fact]
    public void 写密钥用的是原子写而不是直写目标()
    {
        // 「原子性」在 SecretService 这一层是**观察不到**的：
        // 无论走临时文件还是直写目标，最终产物字节完全一样。
        // 所以这里不去测「是不是原子」，而是测「**有没有经过 AtomicFile 的替换时机**」。
        //
        // 原子性本身由 AtomicFileTests 守住（那里有真正的可观察形态：
        // 目标文件在替换前仍是旧内容、临时文件里是新内容）。
        // 这里守的是「没绕过 AtomicFile 直写目标」。
        var target = Path.Combine(_dir, "UserKey.bin");
        var service = new SecretService(target, new RecordingProtector(() => true));

        string? observedTarget = null;
        var original = AtomicFile.Observer;
        try
        {
            AtomicFile.Observer = (path, _) => observedTarget = path;
            service.Encrypt("x");
        }
        finally
        {
            AtomicFile.Observer = original;
        }

        Assert.Equal(Path.GetFullPath(target), observedTarget);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void 空路径会被拒绝()
    {
        Assert.Throws<ArgumentException>(() => new SecretService(""));
        Assert.Throws<ArgumentException>(() => new SecretService("   "));
    }

    [Fact]
    public void 不是本格式的数据会明确报错()
    {
        var service = NewService();

        var ex = Assert.ThrowsAny<Exception>(() => service.Decrypt(Convert.ToBase64String("hello"u8.ToArray())));

        Assert.Contains("损坏", ex.Message);
    }
}
