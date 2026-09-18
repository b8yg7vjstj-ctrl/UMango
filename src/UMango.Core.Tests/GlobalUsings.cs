// 测试工程的全局 using。
//
// ImplicitUsings=enable 已经覆盖 System / System.IO / System.Linq / System.Collections.Generic /
// System.Threading / System.Threading.Tasks 等基础命名空间，这里只补"业务命名空间 + xunit"，
// 免得每个测试文件顶部重复六七行 using（含 System.IO 是必需的：全局 using 与隐式 using 重复声明不冲突）。
global using System.IO;
global using System.Reflection;
global using System.Text.Json;
global using UMango.Core.Execution;
global using UMango.Core.PackageManagers;
global using UMango.Core.PackageManagers.Adapters;
global using UMango.Core.Platform;
global using UMango.Core.Records;
global using UMango.Core.Tests.TestDoubles;
global using Xunit;
