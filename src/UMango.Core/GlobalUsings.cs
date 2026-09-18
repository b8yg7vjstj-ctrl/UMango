// 全局 using。
//
// 为什么需要这个文件：
//   1. 本工程 UseWPF=true，而 WPF 工程的隐式 using 集合里**不包含 System.IO**
//      （与普通 SDK 工程不同）；
//   2. 六个包管理器适配器都要用到进程执行与异常类型，
//      与其在每个适配器文件顶部重复三行 using，不如在这里声明一次。
// 这也是「降低重复代码率」的一部分。
global using System.IO;
global using UMango.Core.Execution;
