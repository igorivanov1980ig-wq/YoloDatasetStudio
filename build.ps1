$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pythonDir = Join-Path $root 'python'
if (-not (Test-Path (Join-Path $pythonDir '.venv'))) { & (Join-Path $pythonDir 'install.ps1') }
$venvPython = Join-Path $pythonDir '.venv\Scripts\python.exe'
& $venvPython -m PyInstaller --clean --onefile --name yolo_worker (Join-Path $pythonDir 'yolo_worker.py')
$publish = Join-Path $root 'publish'
& dotnet publish (Join-Path $root 'DatasetStudio.csproj') -c Release -r win-x64 --self-contained true -o $publish
New-Item -ItemType Directory -Force (Join-Path $publish 'python') | Out-Null
Copy-Item (Join-Path $pythonDir 'dist\yolo_worker.exe') (Join-Path $publish 'python\yolo_worker.exe') -Force
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath (Join-Path $root 'YoloDatasetStudio-win-x64.zip') -Force
Write-Host "Готово: $root\YoloDatasetStudio-win-x64.zip"
