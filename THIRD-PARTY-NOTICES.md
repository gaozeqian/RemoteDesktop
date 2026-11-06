# 第三方组件声明

本产品使用以下开源组件。它们按各自的许可条款提供，版权归各自的作者所有（许可证全文见各组件的项目主页）。
列表取自随安装包发布的程序的依赖闭包（客户端、被控端、信令服务器、中继服务器、运维监控台与 Ops HTTPS 网关），因此也包含只在构建时用到的包。

本文件由 `build/tools/Export-ThirdPartyNotices.ps1` 从 NuGet 元数据生成，请不要手改——改了下次生成就没了。
依赖变化后（例如 Dependabot 升级了某个包）重新运行一次：

```
pwsh -File build/tools/Export-ThirdPartyNotices.ps1
```

此外，程序以自包含方式发布，因此还包含 .NET 运行时（MIT 许可，https://github.com/dotnet/runtime）。

| 组件 | 版本 | 许可 | 项目主页 |
|---|---|---|---|
| Avalonia | 12.1.4 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | 见项目主页 | https://avaloniaui.net/ |
| Avalonia.BuildServices | 11.3.2 | MIT | https://avaloniaui.net/ |
| Avalonia.Desktop | 12.1.4 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.FreeDesktop | 12.1.4 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.FreeDesktop.AtSpi | 12.1.4 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.HarfBuzz | 12.1.4 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.Native | 12.1.4 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.Remote.Protocol | 12.1.4 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.Skia | 12.1.4 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.Themes.Fluent | 12.1.4 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.Win32 | 12.1.4 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.X11 | 12.1.4 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| BouncyCastle.Cryptography | 2.7.0 | MIT | https://www.bouncycastle.org/stable/nuget/csharp/website |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| Concentus | 2.2.2 | 见项目主页 | https://github.com/lostromb/concentus |
| FFmpeg.AutoGen | 8.0.0 | 见项目主页 | https://github.com/Ruslan-B/FFmpeg.AutoGen |
| Google.Protobuf | 3.36.2 | BSD-3-Clause | https://github.com/protocolbuffers/protobuf |
| HarfBuzzSharp | 8.3.1.3 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| HarfBuzzSharp.NativeAssets.Linux | 8.3.1.3 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| HarfBuzzSharp.NativeAssets.macOS | 8.3.1.3 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| HarfBuzzSharp.NativeAssets.WebAssembly | 8.3.1.3 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.3 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| MicroCom.Runtime | 0.11.6 | MIT | https://www.nuget.org/packages/MicroCom.Runtime |
| Microsoft.Data.Sqlite | 10.0.13 | MIT | https://docs.microsoft.com/dotnet/standard/data/sqlite/ |
| Microsoft.Data.Sqlite.Core | 10.0.13 | MIT | https://docs.microsoft.com/dotnet/standard/data/sqlite/ |
| Microsoft.Extensions.Configuration | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Configuration.Binder | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Configuration.CommandLine | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Configuration.EnvironmentVariables | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Configuration.FileExtensions | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Configuration.Json | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Configuration.UserSecrets | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.DependencyInjection | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Diagnostics | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Diagnostics.Abstractions | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.FileProviders.Physical | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.FileSystemGlobbing | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Hosting | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Hosting.Systemd | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Hosting.WindowsServices | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Logging | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Logging.Abstractions | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Logging.Configuration | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Logging.Console | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Logging.Debug | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Logging.EventLog | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Logging.EventSource | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Options | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Options.ConfigurationExtensions | 10.0.14 | MIT | https://dot.net/ |
| Microsoft.Extensions.Primitives | 10.0.14 | MIT | https://dot.net/ |
| NAudio.Core | 3.2.0 | MIT | https://naudio.github.io/NAudio/ |
| NAudio.Wasapi | 3.2.0 | MIT | https://naudio.github.io/NAudio/ |
| Npgsql | 10.0.3 | PostgreSQL | https://github.com/npgsql/npgsql |
| Serilog | 4.3.0 | Apache-2.0 | https://serilog.net/ |
| Serilog.Extensions.Hosting | 10.0.0 | Apache-2.0 | https://github.com/serilog/serilog-extensions-hosting |
| Serilog.Extensions.Logging | 10.0.0 | Apache-2.0 | https://github.com/serilog/serilog-extensions-logging |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | https://github.com/serilog/serilog-sinks-file |
| SharpGen.Runtime | 2.4.2-beta | MIT | https://github.com/SharpGenTools/SharpGenTools |
| SharpGen.Runtime.COM | 2.4.2-beta | MIT | https://github.com/SharpGenTools/SharpGenTools |
| SkiaSharp | 3.119.4 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| SkiaSharp.NativeAssets.Linux | 3.119.4 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| SkiaSharp.NativeAssets.macOS | 3.119.4 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| SkiaSharp.NativeAssets.WebAssembly | 3.119.4 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| SkiaSharp.NativeAssets.Win32 | 3.119.4 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.13 | Apache-2.0 | https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3 |
| SQLitePCLRaw.core | 2.1.13 | Apache-2.0 | https://www.nuget.org/packages/SQLitePCLRaw.core |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.13 | Apache-2.0 | https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3 |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.13 | Apache-2.0 | https://www.nuget.org/packages/SQLitePCLRaw.provider.e_sqlite3 |
| System.Diagnostics.EventLog | 10.0.14 | MIT | https://dot.net/ |
| System.IO.Hashing | 8.0.0 | MIT | https://dot.net/ |
| System.Numerics.Tensors | 9.0.0 | MIT | https://dot.net/ |
| System.Security.Cryptography.ProtectedData | 10.0.13 | MIT | https://dot.net/ |
| System.ServiceProcess.ServiceController | 10.0.14 | MIT | https://dot.net/ |
| Tmds.DBus.Protocol | 0.95.1 | MIT | https://www.nuget.org/packages/Tmds.DBus.Protocol |
| Vortice.Direct3D11 | 3.8.3 | MIT | https://www.nuget.org/packages/Vortice.Direct3D11 |
| Vortice.DirectX | 3.8.3 | MIT | https://www.nuget.org/packages/Vortice.DirectX |
| Vortice.DXGI | 3.8.3 | MIT | https://www.nuget.org/packages/Vortice.DXGI |
| Vortice.Mathematics | 2.1.0 | MIT | https://github.com/amerkoleci/Vortice.Mathematics |
| Vortice.MediaFoundation | 3.8.3 | MIT | https://www.nuget.org/packages/Vortice.MediaFoundation |
| Yarp.ReverseProxy | 2.3.0 | MIT | https://github.com/dotnet/yarp |

