# Agent 系统信息采集规范化设计

- 日期：2026-11-28
- 项目：server-node-monitor
- 状态：待用户审阅
- 范围：仅修改 Agent 系统信息采集逻辑，不修改后端节点协议

## 1. 背景

当前 Agent 通过 `RegisterDto[3]` 上报 `Os`：

- Linux 读取 `/etc/os-release` 的 `PRETTY_NAME`；
- Windows 读取注册表中的 `ProductName`、`DisplayVersion` 和构建号；
- Master 将其写入节点硬件信息，Emerald 主题依据该字符串选择系统图标。

Linux 不同发行版的 `PRETTY_NAME` 可能包含不同前缀、厂商定制文本或不一致的版本描述，导致同一发行版无法稳定匹配。该问题不需要新增协议字段。

## 2. 目标

1. 保留 `RegisterDto` 的协议版本、字段编号和字段类型不变。
2. 将常见 Linux 发行版规范化为稳定的人类可读系统名称。
3. 保留版本信息；在可安全提取时输出发行版版本号。
4. 对未知、缺失或损坏的 `os-release` 内容提供确定性回退。
5. 不改变 Windows 现有采集逻辑的语义。
6. 通过 Agent 单元测试覆盖正常、缺失、损坏和未知输入。

## 3. 非目标

- 不新增 `OsFamily`、`OsVersion`、`OsId` 等协议字段。
- 不提升协议版本，不修改 MessagePack key，不改数据库表结构或迁移。
- 不通过公网 IP 或 GeoIP 推断操作系统。
- 不修改 Emerald 前端的图标映射规则。

## 4. 设计

### 4.1 解析器位置

在 Agent 采集器共享代码中增加纯函数式 OS release 解析器，LinuxSystemInfo 只负责读取文件并调用解析器。解析器不访问网络、不读取数据库，便于单元测试。

### 4.2 输入解析

逐行解析 `KEY=VALUE`：

- 忽略空行、注释和没有 `=` 的行；
- 支持双引号、单引号和未加引号的值；
- 仅去除外层引号，不修改值内部内容；
- 读取 `ID`、`NAME`、`VERSION_ID`、`PRETTY_NAME`。

### 4.3 输出优先级

1. 识别的 `ID` + `VERSION_ID`：生成规范名称，例如 `Debian 12`、`Ubuntu 24.04`、`Alpine Linux 3.20`。
2. 只有 `NAME` 与版本信息：规范化 `NAME` 后组合版本。
3. 只有 `PRETTY_NAME`：清理空白并使用其内容。
4. 无法解析：回退到 `RuntimeInformation.OSDescription`。

为避免显示异常，最终字符串继续经过现有 `Clip(..., 128)` 限制。

### 4.4 发行版名称映射

至少覆盖：

| `ID` | 输出名称 |
|---|---|
| `debian` | Debian |
| `ubuntu` | Ubuntu |
| `alpine` | Alpine Linux |
| `rocky` | Rocky Linux |
| `almalinux` / `alma` | AlmaLinux |
| `centos` | CentOS |
| `rhel` | Red Hat Enterprise Linux |
| `fedora` | Fedora |
| `arch` | Arch Linux |
| `opensuse-leap` / `opensuse-tumbleweed` | openSUSE |
| `amzn` | Amazon Linux |
| `ol` | Oracle Linux |

未知 `ID` 使用清理后的 `NAME`，不丢失原始发行版名称。

### 4.5 兼容性

- `RegisterDto[3] Os` 继续使用相同的 MessagePack key 和字符串类型。
- Master 现有 `Node.Os` 数据无需迁移；新注册或重新注册的节点会逐步获得规范值。
- 旧 Agent 上报的旧格式继续被 Master 接受。
- Windows 采集器保持现有输出格式，继续读取产品名、显示版本和构建号。

## 5. 测试设计

新增 Agent 测试：

1. Debian `ID=debian`, `VERSION_ID=12` → `Debian 12`。
2. Ubuntu 带 `PRETTY_NAME` 和 LTS 描述 → `Ubuntu 24.04` 或约定的稳定版本格式。
3. Alpine → `Alpine Linux <version>`。
4. Rocky/Alma/CentOS 等别名映射。
5. 未知 ID 使用 `NAME` / `PRETTY_NAME` 回退。
6. 缺少 `PRETTY_NAME` 仍能由 ID、NAME、VERSION_ID 生成结果。
7. 引号、空格、注释和 malformed 行不会导致异常。
8. 完全不可解析时回退到运行时 OS 描述。
9. 现有协议 formatter、Agent 集成测试和 Master 测试保持通过。

## 6. 验收标准

- Agent 测试全部通过。
- `RegisterDto.FieldCount`、`[Key(3)] Os` 和 `ProtocolConstants.ProtocolVersion` 无变化。
- Debian、Ubuntu、Windows 节点在 Emerald 中仍能显示对应系统图标和名称。
- `dotnet test ServerNodeMonitor.slnx -c Release` 通过。
- 运行基线、修改后、回滚副本的相同采集输入，输出分别符合预期，回滚副本字节哈希恢复到基线。

## 7. 回滚与发布

代码只涉及 Agent 采集器与测试；回滚为还原采集器文件和测试文件，不涉及数据库迁移。发布时沿用现有 Release 工作流，旧 Agent 与旧节点数据无需特殊处理。
