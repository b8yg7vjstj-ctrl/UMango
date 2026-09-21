using System.Security.Cryptography;

namespace UMango.Core.Hash;

/// <summary>
/// 哈希计算服务。提供 MD5/SHA1/SHA256/SHA512，返回十六进制字符串。
/// 使用场景：下载文件校验、缓存键生成。
/// </summary>
public static class HashService
{
    public static string ComputeMd5(byte[] data) => ToHex(MD5.HashData(data));
    public static string ComputeMd5File(string path) => ToHex(MD5.HashData(File.ReadAllBytes(path)));

    public static string ComputeSha1(byte[] data) => ToHex(SHA1.HashData(data));
    public static string ComputeSha1File(string path) => ToHex(SHA1.HashData(File.ReadAllBytes(path)));

    public static string ComputeSha256(byte[] data) => ToHex(SHA256.HashData(data));
    public static string ComputeSha256Text(string text) => ToHex(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    public static string ComputeSha256File(string path) => ToHex(SHA256.HashData(File.ReadAllBytes(path)));

    public static string ComputeSha512(byte[] data) => ToHex(SHA512.HashData(data));
    public static string ComputeSha512Text(string text) => ToHex(SHA512.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private static string ToHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
}
