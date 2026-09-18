// 程序集级测试配置。
//
// 为什么关闭并行：
//   1. InstallRecordStore.StorePath 是**静态只读**属性（不支持注入路径），
//      要避免测试写真实用户目录，只能把进程级环境变量 HOME/USERPROFILE/APPDATA 指向临时目录；
//   2. 「CLI 不在 PATH 时返回 NotInstalled」用例需要把进程级 PATH 换成空目录。
// 环境变量是进程全局的，xunit 默认按测试类并行执行会互相污染，因此整体串行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
