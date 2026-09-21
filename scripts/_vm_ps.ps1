$ErrorActionPreference = 'SilentlyContinue'
Write-Output "=== 最近的应用程序错误（SideBySide / 应用崩溃）==="
Get-WinEvent -LogName Application -MaxEvents 40 |
  Where-Object { $_.ProviderName -match 'SideBySide|Application Error|\.NET' -or $_.Message -match 'UMango' } |
  Select-Object -First 4 |
  ForEach-Object {
    Write-Output ("--- " + $_.TimeCreated.ToString() + " [" + $_.ProviderName + "]")
    Write-Output (($_.Message -split "`r?`n") | Select-Object -First 8 | Out-String)
  }
