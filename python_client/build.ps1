$ErrorActionPreference = "Stop"
$projectDir = $PSScriptRoot
Push-Location $projectDir
try {
    python -m pip install -r requirements.txt
    python -m PyInstaller --noconfirm --clean --windowed --name Se7enPro `
        --add-data "..\PsiphonUI\Resources;Resources" `
        --add-data "..\PsiphonUI\Assets;Assets" `
        run_app.py
    Write-Host "Output: $projectDir\dist\Se7enPro\Se7enPro.exe" -ForegroundColor Green
}
finally {
    Pop-Location
}
