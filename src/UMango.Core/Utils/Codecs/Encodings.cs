// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/Utils/Codecs/Encodings.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.Utils.Codecs → UMango.Core.Utils.Codecs。
//   2. **修掉原版的运行期崩溃**：原版用
//      `Encoding.GetEncoding("GB18030")` 作为**静态字段初始化**。
//      .NET Core / .NET 5+ 默认**不**提供 GB18030 与代码页 936，
//      不注册 CodePagesEncodingProvider 的话这一行直接抛
//      `ArgumentException: 'GB18030' is not a supported encoding name`。
//      PCL 靠 `StartupService` / `Application.OnStartup` 里的
//      `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`
//      才不炸 —— 也就是说**任何在启动流程之前碰这里的代码都会崩**。
//      UMango 改成惰性注册：第一次真正要用时才注册，且注册失败时
//      返回一个**不会抛异常**的替代编码（Latin1，保证不丢字节）。
//   3. 补 XML 文档。

using System.Text;

namespace UMango.Core.Utils.Codecs;

/// <summary>
/// 常用编码的惰性提供者。
///
/// 为什么要惰性注册 CodePages：GB18030 / GBK 这类代码页在 .NET Core 上
/// 默认不可用，必须显式 <see cref="CodePagesEncodingProvider"/> 注册。
/// PCL 把注册放在启动流程里，于是"启动前"任何一处访问都会崩；
/// UMango 把它收进这里，谁用谁负责，不存在"忘了注册"的时间窗。
/// </summary>
public static class Encodings
{
    private static readonly object Gate = new();
    private static Encoding? _gb18030;

    /// <summary>
    /// GB18030（代码页 54936）—— GBK 的超集，兼容简体中文。
    ///
    /// 拿不到时返回 **Latin1**（每字节一字符，映射 1:1），
    /// 它不会丢数据，只是看不懂中文 —— 这比抛异常有用得多。
    /// </summary>
    public static Encoding GB18030
    {
        get
        {
            lock (Gate)
            {
                return _gb18030 ??= ResolveGb18030();
            }
        }
    }

    /// <summary>GB18030 是否真的可用（而不是 Latin1 替代品）。</summary>
    public static bool HasGb18030
    {
        get
        {
            var encoding = GB18030;
            return encoding.CodePage == 54936;
        }
    }

    /// <summary>
    /// GBK（代码页 936）—— 简体中文最常见编码。
    ///
    /// 与 GB18030 分开是因为 GB18030 是 GBK 的超集：只判 GB18030 的话
    /// GBK 文本也会"判成功"，编码名就永远分不出 GBK / GB18030。
    /// </summary>
    public static Encoding Gbk
    {
        get
        {
            lock (Gate)
            {
                return _gbk ??= ResolveGbk();
            }
        }
    }

    private static Encoding? _gbk;

    /// <summary>
    /// 确保代码页提供器已注册。
    ///
    /// 幂等：重复调用无副作用。注册失败（受限平台）**不抛异常** ——
    /// 让调用方走 Latin1 兜底路径。
    /// </summary>
    public static void EnsureCodePagesRegistered()
    {
        lock (Gate)
        {
            if (_gb18030 is not null) return;
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            }
            catch (Exception)
            {
                // 受限环境下注册可能失败 —— 交给 ResolveGb18030 走替代路径
            }
        }
    }

    private static Encoding ResolveGb18030()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030");
        }
        catch (Exception)
        {
            return Encoding.Latin1;
        }
    }

    private static Encoding ResolveGbk()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936);
        }
        catch (Exception)
        {
            return Encoding.Latin1;
        }
    }
}
