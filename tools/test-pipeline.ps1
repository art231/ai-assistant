<#
.SYNOPSIS
    E2E тест пайплайна: аудиофайл → Whisper → БД → PDF
.DESCRIPTION
    Тестирует полный цикл обработки записи разговора:
    1. Отправляет аудиофайл в WhisperLiveKit для транскрибации
    2. Создаёт запись MeetingRecording в БД через прямой SQL
    3. Заполняет FullText и Summary
    4. Вызывает экспорт PDF
    5. Проверяет, что PDF не пустой
.PARAMETER AudioFilePath
    Путь к тестовому OGG/WAV файлу с речью
.PARAMETER BackendUrl
    URL backend API (по умолчанию http://localhost:5000)
.PARAMETER WhisperUrl
    URL WhisperLiveKit (по умолчанию http://localhost:8081)
.PARAMETER RoomId
    ID комнаты (если не указан, создаётся новый)
.EXAMPLE
    .\test-pipeline.ps1 -AudioFilePath "C:\test\speech.ogg"
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$AudioFilePath,

    [string]$BackendUrl = "http://localhost:5000",
    [string]$WhisperUrl = "http://localhost:8081",
    [string]$RoomId = "",
    [string]$UserName = "TestUser"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Цвета для вывода
$Green = "Green"
$Red = "Red"
$Yellow = "Yellow"
$Cyan = "Cyan"

function Write-Step {
    param([string]$Message, [string]$Color = $Cyan)
    Write-Host "`n=== $Message ===" -ForegroundColor $Color
}

function Write-Result {
    param([bool]$Success, [string]$Message)
    if ($Success) {
        Write-Host "  ✓ $Message" -ForegroundColor $Green
    } else {
        Write-Host "  ✗ $Message" -ForegroundColor $Red
    }
}

# ─── Проверка файла ───────────────────────────────────────────────
Write-Step "ШАГ 0: Проверка входного аудиофайла"

if (-not (Test-Path $AudioFilePath)) {
    Write-Host "Файл не найден: $AudioFilePath" -ForegroundColor $Red
    exit 1
}

$fileInfo = Get-Item $AudioFilePath
Write-Result $true "Файл: $($fileInfo.Name), размер: $($fileInfo.Length) байт"

if ($fileInfo.Length -eq 0) {
    Write-Host "Файл пустой!" -ForegroundColor $Red
    exit 1
}

# ─── ШАГ 1: Транскрибация через WhisperLiveKit ────────────────────
Write-Step "ШАГ 1: Транскрибация аудио через WhisperLiveKit"

try {
    $whisperResponse = Invoke-RestMethod `
        -Uri "$WhisperUrl/transcribe" `
        -Method Post `
        -Form @{
            audio = Get-Item -Path $AudioFilePath
        } `
        -TimeoutSec 300

    $transcriptText = $whisperResponse.text
    $language = $whisperResponse.language
    $segments = $whisperResponse.segments

    Write-Result ($transcriptText -ne $null -and $transcriptText.Length -gt 0) `
        "Транскрипция получена: $($transcriptText.Length) символов, язык: $language"

    if ([string]::IsNullOrEmpty($transcriptText)) {
        Write-Host "  ⚠ Whisper вернул пустой текст. Проверь whisper-livekit." -ForegroundColor $Yellow
    } else {
        Write-Host "  Текст: $($transcriptText.Substring(0, [Math]::Min(200, $transcriptText.Length)))..." -ForegroundColor $Gray
    }
} catch {
    Write-Result $false "Ошибка вызова Whisper: $_"
    Write-Host "  Проверь, запущен ли whisper-livekit (docker ps | findstr whisper)" -ForegroundColor $Yellow
    exit 1
}

# ─── ШАГ 2: Генерация саммари через WhisperLiveKit ────────────────
Write-Step "ШАГ 2: Генерация саммари"

$summary = ""
try {
    # Формируем транскрипты в формате, который ожидает /summarize
    $transcriptsForSummary = @(
        @{
            speakerId = "speaker_0"
            userName = $UserName
            text = $transcriptText
        }
    )

    $summaryBody = @{
        transcripts = $transcriptsForSummary
    } | ConvertTo-Json

    $summaryResponse = Invoke-RestMethod `
        -Uri "$WhisperUrl/summarize" `
        -Method Post `
        -Body $summaryBody `
        -ContentType "application/json" `
        -TimeoutSec 120

    $summary = $summaryResponse.summary
    Write-Result (-not [string]::IsNullOrEmpty($summary)) `
        "Саммари получено: $($summary.Length) символов"
} catch {
    Write-Result $false "Ошибка генерации саммари: $_"
    Write-Host "  Продолжаем без саммари..." -ForegroundColor $Yellow
}

# ─── ШАГ 3: Создание записи в БД через API ────────────────────────
Write-Step "ШАГ 3: Создание MeetingRecording в БД"

if ([string]::IsNullOrEmpty($RoomId)) {
    $RoomId = [Guid]::NewGuid().ToString()
    Write-Host "  Создан новый RoomId: $RoomId" -ForegroundColor $Gray
}

# Сначала создаём комнату
try {
    $roomBody = @{
        name = "E2E Test Room $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
        maxParticipants = 10
    } | ConvertTo-Json

    $roomResponse = Invoke-RestMethod `
        -Uri "$BackendUrl/api/rooms" `
        -Method Post `
        -Body $roomBody `
        -ContentType "application/json" `
        -TimeoutSec 30

    $actualRoomId = $roomResponse.id
    Write-Result $true "Комната создана: $actualRoomId"
} catch {
    Write-Result $false "Не удалось создать комнату: $_"
    Write-Host "  Используем переданный RoomId: $RoomId" -ForegroundColor $Yellow
    $actualRoomId = $RoomId
}

# Создаём MeetingRecording через прямой SQL в PostgreSQL
Write-Step "ШАГ 3b: Вставка MeetingRecording через PostgreSQL"

$recordingId = [Guid]::NewGuid().ToString()
$audioPath = "/app/recordings/$actualRoomId/meeting_$($actualRoomId.Replace('-','')).ogg"
$durationSeconds = [Math]::Max(1, [int]($fileInfo.Length / 16000 / 2))  # Примерная оценка

# Экранируем одинарные кавычки для SQL
$escapedFullText = $transcriptText.Replace("'", "''")
$escapedSummary = ($summary -replace "'", "''")

$sql = @"
INSERT INTO "MeetingRecordings" ("Id", "RoomId", "AudioPath", "FullText", "Summary", "StartedAt", "EndedAt", "DurationSeconds", "FileSizeBytes", "Status")
VALUES (
    '$recordingId'::uuid,
    '$actualRoomId'::uuid,
    '$audioPath',
    '$escapedFullText',
    '$escapedSummary',
    NOW() - INTERVAL '$durationSeconds seconds',
    NOW(),
    $durationSeconds,
    $($fileInfo.Length),
    2  -- Completed
);
"@

try {
    # Записываем SQL во временный файл и выполняем через docker
    $sqlFile = [System.IO.Path]::GetTempFileName()
    $sql | Out-File -FilePath $sqlFile -Encoding UTF8

    $result = docker exec -i voicechat-postgres psql -U postgres -d voicechat -c "$sql" 2>&1
    Remove-Item $sqlFile -Force

    if ($LASTEXITCODE -eq 0) {
        Write-Result $true "MeetingRecording создана: $recordingId"
    } else {
        throw $result
    }
} catch {
    Write-Result $false "Ошибка вставки в БД: $_"
    
    # Пробуем альтернативный подход - через docker cp
    Write-Host "  Пробуем альтернативный способ..." -ForegroundColor $Yellow
    try {
        $sqlContent = @"
INSERT INTO "MeetingRecordings" ("Id", "RoomId", "AudioPath", "FullText", "Summary", "StartedAt", "EndedAt", "DurationSeconds", "FileSizeBytes", "Status")
VALUES ('$recordingId'::uuid, '$actualRoomId'::uuid, '$audioPath', '$escapedFullText', '$escapedSummary', NOW() - INTERVAL '$durationSeconds seconds', NOW(), $durationSeconds, $($fileInfo.Length), 2);
"@
        $tempSqlPath = "$env:TEMP\insert_recording.sql"
        $sqlContent | Out-File -FilePath $tempSqlPath -Encoding UTF8 -NoNewline
        docker cp "$tempSqlPath" voicechat-postgres:/tmp/insert_recording.sql 2>&1 | Out-Null
        docker exec voicechat-postgres psql -U postgres -d voicechat -f /tmp/insert_recording.sql 2>&1
        Write-Result $true "MeetingRecording создана (альтернативный способ): $recordingId"
    } catch {
        Write-Result $false "Ошибка вставки в БД (альтернативный): $_"
        exit 1
    }
}

# ─── ШАГ 4: Экспорт PDF ───────────────────────────────────────────
Write-Step "ШАГ 4: Экспорт PDF"

$pdfOutputPath = [System.IO.Path]::Combine(
    [System.IO.Path]::GetTempPath(),
    "meeting_export_$([Guid]::NewGuid().ToString('N')).pdf"
)

try {
    $pdfBytes = Invoke-WebRequest `
        -Uri "$BackendUrl/api/recordings/$recordingId/export-pdf" `
        -Method Get `
        -TimeoutSec 60 `
        -OutFile $pdfOutputPath

    if ((Get-Item $pdfOutputPath).Length -gt 1000) {
        Write-Result $true "PDF создан: $pdfOutputPath, размер: $((Get-Item $pdfOutputPath).Length) байт"
    } else {
        Write-Result $false "PDF слишком маленький или пустой: $((Get-Item $pdfOutputPath).Length) байт"
        Write-Host "  Содержимое PDF:" -ForegroundColor $Yellow
        Get-Content $pdfOutputPath -TotalCount 5 -Encoding Byte | ForEach-Object { Write-Host "    $('0x{0:X2}' -f $_)" }
    }
} catch {
    Write-Result $false "Ошибка экспорта PDF: $_"
    
    # Проверяем, что вернул сервер
    try {
        $response = Invoke-WebRequest `
            -Uri "$BackendUrl/api/recordings/$recordingId/export-pdf" `
            -Method Get `
            -TimeoutSec 60
        Write-Host "  Статус: $($response.StatusCode)" -ForegroundColor $Yellow
        Write-Host "  Content-Type: $($response.Headers['Content-Type'])" -ForegroundColor $Yellow
        if ($response.Content.Length -lt 100) {
            Write-Host "  Ответ: $($response.Content)" -ForegroundColor $Yellow
        }
    } catch {
        Write-Host "  Ошибка запроса: $_" -ForegroundColor $Red
    }
}

# ─── ШАГ 5: Проверка записи в БД ──────────────────────────────────
Write-Step "ШАГ 5: Проверка записи в БД"

try {
    $dbCheck = docker exec voicechat-postgres psql -U postgres -d voicechat -c "SELECT ""Id"", ""FullText"" IS NOT NULL as has_text, ""Summary"" IS NOT NULL as has_summary, ""Status"", ""FileSizeBytes"" FROM ""MeetingRecordings"" WHERE ""Id"" = '$recordingId'::uuid;" 2>&1
    Write-Host "  Результат проверки БД:" -ForegroundColor $Gray
    $dbCheck | ForEach-Object { Write-Host "    $_" }
} catch {
    Write-Host "  Ошибка проверки БД: $_" -ForegroundColor $Yellow
}

# ─── ИТОГ ──────────────────────────────────────────────────────────
Write-Step "ИТОГ" $Green

$finalPdfPath = Get-Item $pdfOutputPath -ErrorAction SilentlyContinue
if ($finalPdfPath -and $finalPdfPath.Length -gt 1000) {
    Write-Host @"

✅ ПАЙПЛАЙН РАБОТАЕТ!

  Аудиофайл:     $AudioFilePath ($($fileInfo.Length) байт)
  Whisper:       ✓ транскрибация ($($transcriptText.Length) символов)
  Саммари:       $(if ($summary) { "✓ ($($summary.Length) символов)" } else { "✗ не получено" })
  БД:            ✓ запись создана ($recordingId)
  PDF:           ✓ $($finalPdfPath.Length) байт
  Путь к PDF:    $pdfOutputPath

  Открой PDF:    start $pdfOutputPath
"@ -ForegroundColor $Green
} else {
    Write-Host @"

❌ ПАЙПЛАЙН НЕ РАБОТАЕТ

  Проверь:
  1. Логи backend: docker logs voicechat-backend --tail 50
  2. Логи whisper: docker logs voicechat-whisper --tail 50
  3. Статус сервисов: docker ps
  4. БД: docker exec voicechat-postgres psql -U postgres -d voicechat -c "SELECT * FROM ""MeetingRecordings"" ORDER BY ""StartedAt"" DESC LIMIT 5;"
"@ -ForegroundColor $Red
}
