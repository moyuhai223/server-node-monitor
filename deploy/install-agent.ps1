# Server Node Monitor - agent installer for Windows x64 (run in an elevated PowerShell).
#
# Standalone (values from environment variables):
#   $env:SNM_SERVER = 'https://m.example.com'; $env:SNM_KEY = 'snmk_xxxxxxxx'
#   irm https://raw.githubusercontent.com/moyuhai223/server-node-monitor/main/deploy/install-agent.ps1 | iex
# Served by a master with the node's values pre-filled (节点管理 → 安装脚本):
#   irm "https://m.example.com/install/<token>?os=windows" | iex
# Optional:  $env:SNM_PROXY = 'socks5://10.0.0.1:1080'   $env:SNM_NET_IF = 'Ethernet'   $env:SNM_VERSION = 'v1.0.0'
# Uninstall: $env:SNM_UNINSTALL = '1'; irm ... | iex
#
# Rendered {{GENERATED_AT}} by master {{MASTER_VERSION}} for node "{{NODE_NAME}}" (placeholders stay literal in the standalone copy).
$ErrorActionPreference = 'Stop'

function Tpl($v) { if ($v -like '{{*}}') { '' } else { $v } }
$SnmServer   = if ($env:SNM_SERVER) { $env:SNM_SERVER.TrimEnd('/') } else { Tpl '{{SERVER_URL}}' }
$SnmKey      = if ($env:SNM_KEY) { $env:SNM_KEY } else { Tpl '{{AGENT_KEY}}' }
$ReleaseBase = if ($env:SNM_RELEASE_BASE) { $env:SNM_RELEASE_BASE } else { Tpl '{{RELEASE_BASE_URL}}' }
if (-not $ReleaseBase) { $ReleaseBase = 'https://github.com/moyuhai223/server-node-monitor/releases/latest/download' }
$ReleaseBase = $ReleaseBase.TrimEnd('/')

$InstallDir = Join-Path $env:ProgramFiles 'snm-agent'
$DataDir    = Join-Path $env:ProgramData 'snm-agent'
$EnvFile    = Join-Path $DataDir 'agent.env'
$Exe        = Join-Path $InstallDir 'snm-agent.exe'
$TaskName   = 'SNM Agent'
$Proxy      = $env:SNM_PROXY
$NetIf      = $env:SNM_NET_IF
$Version    = $env:SNM_VERSION

function Log($m)  { Write-Host "[snm] $m" -ForegroundColor Green }
function Warn($m) { Write-Host "[snm] $m" -ForegroundColor Yellow }
function Die($m)  { Write-Host "[snm] ERROR: $m" -ForegroundColor Red; exit 1 }

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Die 'please run from an elevated (Administrator) PowerShell' }

if ($env:SNM_UNINSTALL -eq '1') {
  Log "removing scheduled task '$TaskName' and files"
  if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
  }
  Get-Process -Name 'snm-agent' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Remove-Item -Recurse -Force $InstallDir, $DataDir -ErrorAction SilentlyContinue
  Log 'uninstalled'; exit 0
}

if (-not $SnmServer) { Die 'SNM_SERVER is required, e.g. $env:SNM_SERVER = ''https://m.example.com''' }
if ($SnmServer -notmatch '^https?://') { Die 'SNM_SERVER must be an http(s) origin such as https://m.example.com' }
if (-not $SnmKey) { Die 'SNM_KEY is required (create a node in the admin UI to get its snmk_... key)' }
if ($SnmKey -notmatch '^snmk_[A-Za-z0-9_-]{43}$') { Die 'SNM_KEY has an unexpected format (expected snmk_ + 43 characters)' }

$arch = $env:PROCESSOR_ARCHITECTURE
if ($arch -ne 'AMD64') { Die "unsupported architecture: $arch (only win-x64 builds are published)" }
$asset = 'snm-agent-win-x64.zip'
$base = if ($Version) { if ($Version -notmatch '^v') { $Version = "v$Version" }; ($ReleaseBase -replace '/latest/download$', '') + "/download/$Version" } else { $ReleaseBase }
$tmp = Join-Path $env:TEMP ('snm-agent-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
  if ($Proxy -and $Proxy -match '^https?://') { [Net.WebRequest]::DefaultWebProxy = New-Object Net.WebProxy($Proxy) }
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  Log "downloading $base/$asset"
  Invoke-WebRequest -Uri "$base/$asset" -OutFile (Join-Path $tmp $asset) -UseBasicParsing
  Invoke-WebRequest -Uri "$base/$asset.sha256" -OutFile (Join-Path $tmp "$asset.sha256") -UseBasicParsing
  $expected = ((Get-Content (Join-Path $tmp "$asset.sha256") -Raw) -split '\s+')[0].ToLowerInvariant()
  $actual = (Get-FileHash -Algorithm SHA256 (Join-Path $tmp $asset)).Hash.ToLowerInvariant()
  if ($expected -ne $actual) { Die "checksum verification failed ($actual != $expected)" }
  Expand-Archive -Path (Join-Path $tmp $asset) -DestinationPath (Join-Path $tmp 'x') -Force
  $newExe = Get-ChildItem -Path (Join-Path $tmp 'x') -Filter 'snm-agent.exe' -Recurse | Select-Object -First 1
  if (-not $newExe) { Die 'snm-agent.exe not found in the archive' }

  if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue }
  Get-Process -Name 'snm-agent' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 1
  New-Item -ItemType Directory -Force -Path $InstallDir, $DataDir | Out-Null
  Copy-Item -Force $newExe.FullName $Exe
  $ver = & $Exe --version 2>$null
  Log "installed binary $ver"

  $lines = @("SNM_SERVER=$SnmServer", "SNM_KEY=$SnmKey", 'SNM_LOG_LEVEL=info')
  if ($Proxy) { $lines += "SNM_PROXY=$Proxy" }
  if ($NetIf) { $lines += "SNM_NET_IF=$NetIf" }
  if ($env:SNM_NAME) { $lines += "SNM_NAME=$($env:SNM_NAME)" }
  if ($env:SNM_INTERVAL) { $lines += "SNM_INTERVAL=$($env:SNM_INTERVAL)" }
  Set-Content -Path $EnvFile -Value ($lines -join "`r`n") -Encoding ASCII
  # restrict the key file to SYSTEM and Administrators
  $acl = Get-Acl $EnvFile
  $acl.SetAccessRuleProtection($true, $false)
  foreach ($id in @('NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators')) {
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($id, 'FullControl', 'Allow')))
  }
  Set-Acl $EnvFile $acl

  $action = New-ScheduledTaskAction -Execute $Exe -Argument ('run --env-file "' + $EnvFile + '"') -WorkingDirectory $InstallDir
  $trigger = New-ScheduledTaskTrigger -AtStartup
  $settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable
  $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
  Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
  Start-ScheduledTask -TaskName $TaskName
  Start-Sleep -Seconds 2
  $state = (Get-ScheduledTask -TaskName $TaskName).State
  if ($state -eq 'Running') { Log "snm-agent is running ($ver) as scheduled task '$TaskName' -> $SnmServer" } else { Warn "task state is '$state'; check Task Scheduler for details" }
}
finally {
  Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
