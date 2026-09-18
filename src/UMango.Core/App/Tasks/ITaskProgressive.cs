// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（PCL.Core/App/Tasks/ITaskProgressive.cs，Apache-2.0）。
namespace UMango.Core.App.Tasks;

/// <summary>进度改变事件，progress 取值 0.0 ~ 1.0</summary>
public delegate void TaskProgressEvent(double progress);

/// <summary>
/// 可报告进度的任务。**这是「按下载方式列明子项进度」的单项载体**：
/// 例如一次 npm 安装会产生多个子任务（解析依赖 / 下载 tarball / 链接），
/// 每个子任务各自实现本接口，界面按子项逐一显示进度。
/// </summary>
public interface ITaskProgressive
{
    /// <summary>进度改变事件</summary>
    event TaskProgressEvent ProgressChanged;
}
