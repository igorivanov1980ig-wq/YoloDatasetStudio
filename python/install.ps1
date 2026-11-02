$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
python -m venv "$root\.venv"
& "$root\.venv\Scripts\python.exe" -m pip install --upgrade pip
& "$root\.venv\Scripts\python.exe" -m pip install -r "$root\requirements.txt"
Write-Host 'Зависимости установлены.'
