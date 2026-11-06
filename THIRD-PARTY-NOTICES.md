# 第三方组件与参考来源

WattHalo 自有代码使用 MIT。以下依赖不因本项目许可而改变其原有许可。发行包提供依赖清单及可用的原始许可文件，松散 DLL 可替换，不进行不可替换的静态合并。

| 组件 | 许可与源码 |
| --- | --- |
| .NET、WPF、Microsoft.Extensions.*、Microsoft.Data.Sqlite、System.* | MIT；[dotnet/runtime](https://github.com/dotnet/runtime)、[dotnet/wpf](https://github.com/dotnet/wpf)、[dotnet/efcore](https://github.com/dotnet/efcore) |
| LibreHardwareMonitorLib 0.9.6 | MPL-2.0；[对应源码](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6)、[许可](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LICENSE)、[第三方声明](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/THIRD-PARTY-NOTICES.txt)；本项目未修改该库 |
| PawnIO.Modules（经 LHM 内嵌分发） | LGPL-2.1；以 LHM 原始第三方声明与对应模块源码为准，[模块源码](https://github.com/namazso/PawnIO.Modules) |
| PawnIO 驱动 | GPL-2.0-or-later，带独立 IOCTL 通信例外；[源码和例外说明](https://github.com/namazso/PawnIO)、[官方安装器](https://github.com/namazso/PawnIO.Setup)；本发行包不重新分发或修改驱动安装器 |
| SQLitePCLRaw | Apache-2.0；[源码](https://github.com/ericsink/SQLitePCL.raw) |
| SQLite | Public Domain；[说明](https://sqlite.org/copyright.html) |
| HidSharp | Apache-2.0；[源码](https://github.com/IntergatedCircuits/HidSharp) |

NVML 由用户机器上安装的 NVIDIA 驱动提供，本项目通过公开 API 动态调用，不分发 NVIDIA 驱动或 NVML 二进制。[接口文档](https://docs.nvidia.com/deploy/nvml-api/)

完整间接依赖及版本由发行构建从 NuGet 解析结果生成；请同时阅读发行包 `licenses` 目录。若某个包将声明放在外部许可页面，清单会保留许可表达式或链接，不将缺少本地许可文件解释为没有许可义务。

功能与交互研究参考 WattSeal（GPL-3.0）；未复制其实现代码。任务栏分层呈现参考 [agent-speed](https://github.com/zoefix/agent-speed) 的实现，适用以下 MIT 许可。设计参考 ui-ux-pro-max 与 Emil Kowalski 的公开交互指导；没有分发其技能文件。

### agent-speed

Copyright (c) 2026 zoefix

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
