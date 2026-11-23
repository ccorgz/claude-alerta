# Instala ou atualiza o Claude Alerta no Windows 10/11, para o usuario atual (sem administrador).
# Compila o app e o comando com o csc.exe do .NET Framework 4, que ja vem no Windows. Pode rodar de novo para atualizar.
$ErrorActionPreference = 'Stop'
$Aqui = Split-Path -Parent $MyInvocation.MyCommand.Path
$Base = Join-Path $env:USERPROFILE '.claude-alerta'

$Fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $Fw 'csc.exe'))) { $Fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$Csc = Join-Path $Fw 'csc.exe'
if (-not (Test-Path $Csc)) { throw 'Não achei o csc.exe do .NET Framework 4 (vem com o Windows 10/11). Ative o .NET Framework 4.8 em "Recursos do Windows".' }
$Wpf = Join-Path $Fw 'WPF'

New-Item -ItemType Directory -Force (Join-Path $Base 'bin'), (Join-Path $Base 'app') | Out-Null
$Pend = Join-Path $Base 'pendencias.json'
if (-not (Test-Path $Pend)) { [IO.File]::WriteAllText($Pend, '[]') }

Write-Host 'Compilando...'
$comum = @('/nologo', '/optimize+', '/codepage:65001')
& $Csc @comum /target:exe "/out:$Base\bin\alerta.novo.exe" /r:System.Web.Extensions.dll "$Aqui\src\alerta.cs"
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar src\alerta.cs' }
& $Csc @comum /target:winexe "/out:$Base\app\ClaudeAlerta.novo.exe" `
    "/r:$Wpf\PresentationFramework.dll" "/r:$Wpf\PresentationCore.dll" "/r:$Wpf\WindowsBase.dll" "/r:$Fw\System.Xaml.dll" `
    /r:System.Web.Extensions.dll /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    /r:System.DirectoryServices.AccountManagement.dll "$Aqui\src\ClaudeAlerta.cs"
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar src\ClaudeAlerta.cs' }

# troca os binários com o app parado
Get-Process ClaudeAlerta -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
Move-Item -Force "$Base\bin\alerta.novo.exe" "$Base\bin\alerta.exe"
Move-Item -Force "$Base\app\ClaudeAlerta.novo.exe" "$Base\app\ClaudeAlerta.exe"

# hooks globais do Claude Code (%USERPROFILE%\.claude\settings.json), sem mexer no resto do arquivo
& "$Base\bin\alerta.exe" hooks ligar
if ($LASTEXITCODE -ne 0) { throw 'Não consegui ligar os hooks no settings.json' }

# abre no login
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ClaudeAlerta' -Value ('"' + "$Base\app\ClaudeAlerta.exe" + '"')
Start-Process "$Base\app\ClaudeAlerta.exe"

# primeira leitura do uso (fora de uma sessão do Claude Code pode não dar; aí a barra preenche na primeira sessão)
& "$Base\bin\alerta.exe" uso *> $null
if ($LASTEXITCODE -eq 0) { Write-Host 'Uso lido.' } else { Write-Host 'O uso aparece na primeira vez que uma sessão do Claude Code rodar.' }
Write-Host "Pronto. Teste:  `"$Base\bin\alerta.exe`" abrir `"Teste`" --detalhe `"Olá`""
