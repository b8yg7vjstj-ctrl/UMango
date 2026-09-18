// 全局 using（与 UMango.Core 的做法一致）。
//
// 为什么需要：本工程 UseWPF=true，而 WPF 工程的隐式 using 集合里
// **不包含 System.IO**（与普通 SDK 工程不同）。
// 在 Core 里加过一份之后，App 侧又会重复踩同一个坑，所以在两端都声明一次。
global using System.IO;
