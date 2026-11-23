# Remove o Claude Alerta: fecha o app, tira do login, tira os hooks do settings.json e apaga %USERPROFILE%\.claude-alerta.
$Base = Join-Path $env:USERPROFILE '.claude-alerta'
Get-Process ClaudeAlerta -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ClaudeAlerta' -ErrorAction SilentlyContinue
if (Test-Path "$Base\bin\alerta.exe") { & "$Base\bin\alerta.exe" hooks desligar }
Start-Sleep -Milliseconds 600
Remove-Item -Recurse -Force $Base -ErrorAction SilentlyContinue
Write-Host 'Claude Alerta removido.'
