# Windows ARM64 探针

Windows 11 ARM64 使用 `snm-agent-win-arm64.zip`，Windows x64 使用 `snm-agent-win-x64.zip`。安装脚本按操作系统架构选择包，包括在仿真 PowerShell 中运行的情况。旧 Release 没有 ARM64 包，需要指定已包含该产物的版本。

## 本地测试包

本机没有 MSVC 链接器，因此本地 `1.0.0-arm64-preview` 包使用 .NET 10 自包含单文件发布，内含 ARM64 运行时，无需另外安装 .NET。它不是 Native AOT 包；首次运行会解压嵌入的原生运行时。CI 继续采用 Native AOT，新增 `windows-11-arm` / `win-arm64` 构建、ZIP 和 SHA256 产物。CI 的最终构建结果需要在推送后验证。

解压后在该目录打开 PowerShell，先运行不连接服务器的采集检查：

```powershell
.\snm-agent.exe --version
.\snm-agent.exe test
```

正式连接时使用节点管理中生成的真实地址和密钥：

```powershell
.\snm-agent.exe run --server 'https://你的监控域名' --key '节点专属密钥'
```

按 Ctrl+C 停止。若已有安装程序生成的配置，可在有权限读取配置的终端运行：

```powershell
.\snm-agent.exe run --env-file "$env:ProgramData\snm-agent\agent.env"
```

不要直接双击 EXE：默认进入 `run`，没有地址和密钥时会输出用法并以退出码 2 结束。需要保留报错时，从已打开的 PowerShell 运行：

```powershell
.\snm-agent.exe test *> .\agent-test.log
$LASTEXITCODE
```

## 开发验证

```powershell
dotnet publish src/SNM.Agent/SNM.Agent.csproj -c Release -r win-arm64 --self-contained true -p:PublishAot=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-arm64
./scripts/test-agent-windows.ps1 -ExePath artifacts/win-arm64/snm-agent.exe -ExpectedArchitecture win-arm64
```

验证脚本检查 PE 架构、版本命令、完整采集及连接失败后的重连循环，避免只测 `--version` / `test` 而漏掉 `run` 初始化。重连检查使用虚拟密钥和回环地址，不连接真实 Master。
