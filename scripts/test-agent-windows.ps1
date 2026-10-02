param(
  [Parameter(Mandatory)][string]$ExePath,
  [ValidateSet('win-x64', 'win-arm64')][string]$ExpectedArchitecture = 'win-arm64'
)
$ErrorActionPreference = 'Stop'
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path

# Verify the executable itself, rather than the architecture of the test shell.
$stream = [IO.File]::OpenRead($ExePath)
$reader = [IO.BinaryReader]::new($stream)
try {
  $stream.Position = 0x3c
  $peOffset = $reader.ReadInt32()
  $stream.Position = $peOffset
  if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Invalid PE executable' }
  $expectedMachine = if ($ExpectedArchitecture -eq 'win-arm64') { 0xaa64 } else { 0x8664 }
  if ($reader.ReadUInt16() -ne $expectedMachine) { throw "Executable architecture is not $ExpectedArchitecture" }
} finally { $reader.Dispose() }

function Invoke-AgentCheck([string[]]$Arguments, [int]$TimeoutMs = 15000, [switch]$ExpectRunning) {
  $process = [Diagnostics.Process]::new()
  $process.StartInfo.FileName = $ExePath
  $process.StartInfo.UseShellExecute = $false
  $process.StartInfo.CreateNoWindow = $true
  $process.StartInfo.RedirectStandardOutput = $true
  $process.StartInfo.RedirectStandardError = $true
  foreach ($name in @($process.StartInfo.Environment.Keys)) {
    if ($name -like 'SNM_*') { $process.StartInfo.Environment.Remove($name) | Out-Null }
  }
  foreach ($arg in $Arguments) { $process.StartInfo.ArgumentList.Add($arg) }
  try {
    $process.Start() | Out-Null
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $exited = $process.WaitForExit($TimeoutMs)
    if (-not $exited) { $process.Kill(); $process.WaitForExit() }
    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
    if ($ExpectRunning) {
      if ($exited) { throw "Agent run exited prematurely ($($process.ExitCode)): $output" }
      if ($output -notmatch 'starting:' -or $output -notmatch 'reconnecting in') {
        throw "Agent did not reach the reconnect loop: $output"
      }
    } elseif (-not $exited -or $process.ExitCode -ne 0) {
      throw "Agent check failed: $output"
    }
    Write-Host "PASS: $($Arguments[0]) ($ExpectedArchitecture)"
  } finally {
    if ($process.Id -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
  }
}

Invoke-AgentCheck -Arguments @('--version')
Invoke-AgentCheck -Arguments @('test')
# Reserve an ephemeral local port, then release it so connection attempts fail.
# No real master or credentials are involved in this startup regression check.
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
Invoke-AgentCheck -Arguments @('run', '--server', "http://127.0.0.1:$port", '--key', ('snmk_' + ('a' * 43)), '--proxy', 'none') -TimeoutMs 8000 -ExpectRunning
