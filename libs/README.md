# libs/ —— 第三方二进制依赖

这个目录在仓库里是空的（`.gitignore` 已排除 `*.dll`）。编译前需要把 LibreHardwareMonitor 0.9.6 的 net472 包里的 DLL 放进来。

## 获取方式

1. 打开 <https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases>，找到 **0.9.6**，下载 `LibreHardwareMonitor.zip`（6.4 MB）
   - 注意：同一个 Release 下还有一个 `LibreHardwareMonitor.NET.10.zip`，那是 .NET 10 版本，本项目用不了，**要下不带 .NET.10 的那个**
2. 解压，把其中根目录的 `.dll` 复制到本目录（语言子目录 `zh-CN/`、`de/` 等不用管）
3. 回到仓库根目录执行 `build.bat`

LHM 的压缩包里还会带 `LibreHardwareMonitor.exe`、`OxyPlot.dll`、`Aga.Controls.dll`、`Microsoft.Win32.TaskScheduler.dll`，这些是它自己 GUI 用的，本程序不需要，复制过来也不影响编译。

## 编译时会被引用的

| 文件 | 说明 | 许可 |
|---|---|---|
| `LibreHardwareMonitorLib.dll` | 传感器读取的核心库，唯一被 `build.rsp` 直接引用的第三方程序集 | MPL-2.0 |
| `LibreHardwareMonitorLib.xml` | 其 XML 文档（可选，删掉不影响编译） | MPL-2.0 |

## 运行时需要一起分发的

`LibreHardwareMonitorLib.dll` 自身还依赖下列程序集，缺一个就会在启动时 `FileNotFoundException`：

| 文件 | 来源 | 许可 |
|---|---|---|
| `HidSharp.dll` | HidSharp | Apache-2.0 |
| `BlackSharp.Core.dll` | LHM 0.9.6 依赖 | 见 LHM 仓库的第三方声明 |
| `DiskInfoToolkit.dll` | LHM 0.9.6 依赖 | 见 LHM 仓库的第三方声明 |
| `RAMSPDToolkit-NDD.dll` | LHM 0.9.6 依赖 | 见 LHM 仓库的第三方声明 |
| `Microsoft.Bcl.AsyncInterfaces.dll` | NuGet | MIT |
| `Microsoft.Bcl.HashCode.dll` | NuGet | MIT |
| `System.Buffers.dll` | NuGet | MIT |
| `System.CodeDom.dll` | NuGet | MIT |
| `System.Collections.Immutable.dll` | NuGet | MIT |
| `System.Formats.Nrbf.dll` | NuGet | MIT |
| `System.IO.Pipelines.dll` | NuGet | MIT |
| `System.Memory.dll` | NuGet | MIT |
| `System.Numerics.Vectors.dll` | NuGet | MIT |
| `System.Reflection.Metadata.dll` | NuGet | MIT |
| `System.Resources.Extensions.dll` | NuGet | MIT |
| `System.Runtime.CompilerServices.Unsafe.dll` | NuGet | MIT |
| `System.Security.AccessControl.dll` | NuGet | MIT |
| `System.Security.Principal.Windows.dll` | NuGet | MIT |
| `System.Text.Encodings.Web.dll` | NuGet | MIT |
| `System.Text.Json.dll` | NuGet | MIT |
| `System.Threading.AccessControl.dll` | NuGet | MIT |
| `System.Threading.Tasks.Extensions.dll` | NuGet | MIT |

版本号跟随 LHM 0.9.6 的发布包，不要单独升级其中某一个——这几个包之间有版本耦合（尤其是 `System.Text.Json` 与 `System.Memory`）。

## 为什么不入库

- 总计约 5 MB 二进制，Git 仓库里只增不减
- 各组件许可不同，混在源码仓库里容易让使用者误解授权范围
- 上游更新时无需在本地维护一堆 DLL 的变更历史
