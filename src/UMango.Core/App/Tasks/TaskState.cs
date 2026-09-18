// SPDX-License-Identifier: Apache-2.0
//
// 本文件的任务/进度模型**采用自 PCL-CE**（PCL-Community/PCL-CE 的 PCL.Core/App/Tasks/），
// 该目录以 Apache-2.0 授权，允许复用与修改。原始设计：任务状态 + 响应式进度通知。
// UMango 侧的改动：包名与命名空间改为 UMango，补充中文注释说明用途。
namespace UMango.Core.App.Tasks;

/// <summary>
/// 任务状态。影响日志与界面显示效果。
/// </summary>
public enum TaskState
{
    /// <summary>等待中</summary>
    Waiting,
    /// <summary>执行中</summary>
    Running,
    /// <summary>已完成</summary>
    Success,
    /// <summary>已取消</summary>
    Canceled,
    /// <summary>失败</summary>
    Failed
}
