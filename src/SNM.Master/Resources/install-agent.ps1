# Server Node Monitor agent installer (Windows, run in an elevated PowerShell)
# Generated {{GENERATED_AT}} by master {{MASTER_VERSION}} for node "{{NODE_NAME}}"
# Usage:
#   irm "<url>?os=windows" | iex                                   install / upgrade
#   $env:SNM_PROXY = "socks5://10.0.0.1:1080"; irm "<url>?os=windows" | iex
#   $env:SNM_UNINSTALL = "1"; irm "<url>?os=windows" | iex        uninstall
$ErrorActionPreference = 'Stop'

$SnmServer   = '{{SERVER_URL}}'
$SnmKey      = '{{AGENT_KEY}}'
$ReleaseBase = '{{RELEASE_BASE_URL}}'

$InstallDir = Join-Path $env:ProgramFiles 'snm-agent'
$DataDir    = Join-Path $env:ProgramData 'snm-agent'
$EnvFile    = Join-Path $DataDir 'agent.env'
$Exe        = Join-Path $InstallDir 'snm-agent.exe'
$TaskName   = 'SNM Agent'
$Proxy      = $env:SNM_PROXY
$NetIf      = $env:SNM_NET_IF

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

$arch = $env:PROCESSOR_ARCHITECTURE
if ($arch -ne 'AMD64') { Die "unsupported architecture: $arch (only win-x64 builds are published)" }
$asset = 'snm-agent-win-x64.zip'
$tmp = Join-Path $env:TEMP ('snm-agent-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
  if ($Proxy -and $Proxy -match '^https?://') { [Net.WebRequest]::DefaultWebProxy = New-Object Net.WebProxy($Proxy) }
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  Log "downloading $ReleaseBase/$asset"
  Invoke-WebRequest -Uri "$ReleaseBase/$asset" -OutFile (Join-Path $tmp $asset) -UseBasicParsing
  Invoke-WebRequest -Uri "$ReleaseBase/$asset.sha256" -OutFile (Join-Path $tmp "$asset.sha256") -UseBasicParsing
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
  if ($state -eq 'Running') { Log "snm-agent is running ($ver) as scheduled task '$TaskName'" } else { Warn "task state is '$state'; check Task Scheduler for details" }
}
finally {
  Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
