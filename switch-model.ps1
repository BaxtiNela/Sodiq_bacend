# ═══════════════════════════════════════════════════════════════
#  switch-model.ps1  —  Model yuklash va Docker qayta ishga tushirish
#  Ishlatish: .\switch-model.ps1
# ═══════════════════════════════════════════════════════════════

$ErrorActionPreference = "Stop"
$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$ModelsDir  = Join-Path $ScriptDir "models"
$ModelFile  = "qwen2.5-coder-7b-instruct-q4_k_m.gguf"
$ModelPath  = Join-Path $ModelsDir $ModelFile
$ModelUrl   = "https://huggingface.co/Qwen/Qwen2.5-Coder-7B-Instruct-GGUF/resolve/main/$ModelFile"

Write-Host ""
Write-Host "══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Qwen2.5-Coder-7B Model O'rnatuvchi" -ForegroundColor Cyan
Write-Host "══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# ── 1. Model allaqachon bormi? ──────────────────────────────────
if (Test-Path $ModelPath) {
    $sizeMB = [math]::Round((Get-Item $ModelPath).Length / 1MB)
    Write-Host "✓ Model allaqachon mavjud: $ModelFile ($sizeMB MB)" -ForegroundColor Green
    Write-Host ""
} else {
    Write-Host "📥 Model yuklanmoqda..." -ForegroundColor Yellow
    Write-Host "   Manba : $ModelUrl" -ForegroundColor Gray
    Write-Host "   Manzil: $ModelPath" -ForegroundColor Gray
    Write-Host ""

    # models papkasi yo'q bo'lsa yaratish
    if (-not (Test-Path $ModelsDir)) {
        New-Item -ItemType Directory -Path $ModelsDir | Out-Null
    }

    # BITS (Windows built-in) yoki Invoke-WebRequest bilan yuklash
    $startTime = Get-Date
    try {
        # BitsTransfer — progress ko'rsatadi, background da ishlaydi
        Import-Module BitsTransfer -ErrorAction SilentlyContinue
        Start-BitsTransfer -Source $ModelUrl -Destination $ModelPath -DisplayName "Qwen2.5-Coder-7B"
    } catch {
        Write-Host "   BITS ishlamadi, oddiy yuklash..." -ForegroundColor Yellow
        # Fallback: WebClient bilan progress
        $webClient = New-Object System.Net.WebClient
        $webClient.Headers.Add("User-Agent", "Mozilla/5.0")

        $lastPct = -1
        $webClient.DownloadProgressChanged += {
            param($s, $e)
            if ($e.ProgressPercentage -ne $lastPct -and $e.ProgressPercentage % 5 -eq 0) {
                $script:lastPct = $e.ProgressPercentage
                $dlMB = [math]::Round($e.BytesReceived / 1MB)
                $totMB = [math]::Round($e.TotalBytesToReceive / 1MB)
                Write-Progress -Activity "Yuklanyapti..." -Status "$dlMB MB / $totMB MB" -PercentComplete $e.ProgressPercentage
            }
        }

        $downloadTask = $webClient.DownloadFileTaskAsync($ModelUrl, $ModelPath)
        $downloadTask.Wait()
        $webClient.Dispose()
        Write-Progress -Activity "Yuklanyapti..." -Completed
    }

    $elapsed = [math]::Round(((Get-Date) - $startTime).TotalSeconds)
    $sizeMB  = [math]::Round((Get-Item $ModelPath).Length / 1MB)
    Write-Host "✓ Yuklandi: $sizeMB MB ($elapsed soniya)" -ForegroundColor Green
    Write-Host ""
}

# ── 2. Docker tekshirish ─────────────────────────────────────────
Write-Host "🐳 Docker holati tekshirilmoqda..." -ForegroundColor Yellow
try {
    $dockerInfo = docker info 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Docker ishlamayapti" }
    Write-Host "✓ Docker tayyor" -ForegroundColor Green
} catch {
    Write-Host "✗ Docker Desktop ishga tushurilmagan! Iltimos avval Docker Desktop ni oching." -ForegroundColor Red
    Read-Host "Enter bosing..."
    exit 1
}
Write-Host ""

# ── 3. llama-server ni qayta build qilish ───────────────────────
Write-Host "🔨 llama-server qayta qurilmoqda..." -ForegroundColor Yellow
Set-Location $ScriptDir
docker compose build llama-server
if ($LASTEXITCODE -ne 0) {
    Write-Host "✗ Build muvaffaqiyatsiz!" -ForegroundColor Red
    Read-Host "Enter bosing..."
    exit 1
}
Write-Host "✓ Build tayyor" -ForegroundColor Green
Write-Host ""

# ── 4. Barcha servislarni qayta ishga tushirish ──────────────────
Write-Host "🚀 Servislar qayta ishga tushirilmoqda..." -ForegroundColor Yellow
docker compose up -d
if ($LASTEXITCODE -ne 0) {
    Write-Host "✗ Servislar ishga tushmadi!" -ForegroundColor Red
    Read-Host "Enter bosing..."
    exit 1
}
Write-Host ""

# ── 5. Sog'liq tekshirish ────────────────────────────────────────
Write-Host "⏳ LLM server tayyor bo'lishi kutilmoqda (90 soniya max)..." -ForegroundColor Yellow
$ready = $false
for ($i = 0; $i -lt 18; $i++) {
    Start-Sleep -Seconds 5
    try {
        $resp = Invoke-WebRequest -Uri "http://localhost:8080/v1/models" -TimeoutSec 3 -UseBasicParsing -ErrorAction SilentlyContinue
        if ($resp.StatusCode -eq 200) {
            $ready = $true
            break
        }
    } catch { }
    Write-Host "   Kutilmoqda... ($([int]($i+1)*5)s)" -ForegroundColor Gray
}

Write-Host ""
if ($ready) {
    Write-Host "══════════════════════════════════════════" -ForegroundColor Green
    Write-Host "  ✓ TAYYOR! Yangi model ishga tushdi." -ForegroundColor Green
    Write-Host "    Model : Qwen2.5-Coder-7B-Instruct" -ForegroundColor Green
    Write-Host "    LLM   : http://localhost:8080" -ForegroundColor Green
    Write-Host "    App   : WindowsAssistantNext.exe ni qayta yoqing" -ForegroundColor Green
    Write-Host "══════════════════════════════════════════" -ForegroundColor Green
} else {
    Write-Host "⚠ Server hali ham tayyor emas. Docker Desktop da loglarni tekshiring:" -ForegroundColor Yellow
    Write-Host "  docker logs wa-llm" -ForegroundColor Gray
}

Write-Host ""
Read-Host "Enter bosing..."
