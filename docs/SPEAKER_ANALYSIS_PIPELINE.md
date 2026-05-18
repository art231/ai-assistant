# Speaker Analysis Pipeline — Техническое задание

## Цель
Создать систему анализа спикеров в реальном времени, которая:
- Определяет **сколько человек участвуют** в диалоге
- Определяет **пол спикера** (мужчина/женщина)
- Определяет **усталость спикера**
- Рекомендует **сделать перерыв** для качественного продолжения беседы
- Создаёт **краткий конспект** встречи после её завершения

---

## Архитектура (полная)

```
┌─────────────────────────────────────────────────────────────────────┐
│                        РЕАЛЬНОЕ ВРЕМЯ                               │
│                                                                     │
│  Микрофон → Mediasoup → AudioChunk → VAD (Silero) → RabbitMQ       │
│                                                          ↓          │
│                                              faster-whisper          │
│                                                          ↓          │
│                                              Текст (транскрипт)     │
│                                                          ↓          │
│  ┌──────────────────────────────────────────────────────┐           │
│  │  AiOrchestratorService (каждые 30-60с)               │           │
│  │  ┌────────────────────────────────────────────────┐  │           │
│  │  │ 1. GenerateSummary (конспект)                  │  │           │
│  │  │ 2. DetectTopicChange (смена темы)              │  │           │
│  │  │ 3. AnalyzeSpeaker (усталость, пол, перерыв)    │  │           │
│  │  │ 4. GenerateAdvice (советы)                     │  │           │
│  │  └────────────────────────────────────────────────┘  │           │
│  └──────────────────────────────────────────────────────┘           │
│                             ↓                                      │
│                       SignalR → Фронтенд                            │
│                                                                     │
├─────────────────────────────────────────────────────────────────────┤
│                        ПОСЛЕ ВСТРЕЧИ                                │
│                                                                     │
│  OGG-файл на диске                                                  │
│       ↓                                                             │
│  WhisperService (HTTP → faster-whisper)                             │
│       ↓                                                             │
│  Полный текст → сохраняется в MeetingRecording.FullText             │
│       ↓                                                             │
│  Ollama → GenerateSummaryAsync                                      │
│       ↓                                                             │
│  Конспект → сохраняется в MeetingRecording.Summary                  │
│       ↓                                                             │
│  Доступен через GET /api/recordings/{id}                            │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Этапы реализации

### Этап 1 — Базовая аналитика (текст) — 2-3 дня

**Цель:** Получить работающий пайплайн на существующей инфраструктуре.

#### Что делаем:

1. **Замена openai-whisper → faster-whisper** в `whisper-livekit`
   - faster-whisper в 4-5 раз быстрее, меньше памяти
   - Использует CTranslate2 для инференса
   - Поддерживает все модели Whisper (tiny, base, small, medium, large)

2. **Добавление VAD (Silero VAD)**
   - Отсеивает тишину перед отправкой в Whisper
   - Обрабатывает 30ms чанки за <1ms на CPU
   - Снижает нагрузку на транскрипцию в 2-3 раза

3. **HTTP-эндпоинт POST /transcribe** в `whisper-livekit`
   - Принимает OGG-файл (multipart/form-data)
   - Возвращает полный текст транскрипции
   - Нужен для offline-обработки записанных встреч

4. **WhisperService.cs** (C#)
   - HTTP-клиент для вызова Whisper (через RabbitMQ или HTTP)
   - Метод `TranscribeAsync(string audioFilePath) → string`

5. **AnalyzeSpeakerAsync** в `OllamaService.cs`
   - Промпт для Llama 3:
   ```
   Analyze the following meeting transcript and determine:
   1. How many speakers are participating?
   2. What is the likely gender of each speaker?
   3. Is any speaker showing signs of fatigue?
   4. Should the meeting take a break?
   5. Should the discussion be postponed?

   Transcript:
   {текст}

   Respond in JSON format:
   {
     "speakerCount": number,
     "speakers": [
       {"id": "speaker_0", "gender": "male/female", "fatigueLevel": 0.0-1.0}
     ],
     "needsBreak": true/false,
     "breakReason": "string",
     "shouldPostpone": true/false,
     "postponeReason": "string"
   }
   ```

6. **Модификация AiOrchestratorService**
   - Добавить вызов `AnalyzeSpeakerAsync` каждые 30-60 секунд
   - Отправлять результат через SignalR событие `SpeakerAnalysis`

7. **Модификация PostProcessRecordingAsync**
   - После остановки записи: OGG → WhisperService → полный текст
   - Затем: полный текст → Ollama → конспект
   - Сохранить в MeetingRecording.FullText и MeetingRecording.Summary

8. **Фронтенд**
   - Подписка на событие `SpeakerAnalysis`
   - Отображение: количество участников, пол, уровень усталости
   - Уведомление: "Рекомендуется сделать перерыв"

---

### Этап 2 — Diarization (разделение спикеров) — после Этапа 1

**Цель:** Точное разделение, кто и когда говорит.

#### Что делаем:

1. **Добавление Diart** в `whisper-livekit`
   - Real-time diarization на основе pyannote.audio
   - Присваивает метки: speaker_0, speaker_1, ...
   - Работает параллельно с Whisper

2. **Модификация пайплайна:**
   ```
   Audio → VAD → Diart (разделение) → faster-whisper (для каждого) → Ollama
   ```

3. **Анализ каждого спикера отдельно**
   - "Иван устал, Мария активна"
   - Индивидуальные метрики усталости

---

### Этап 3 — Голосовые метрики (опционально)

**Цель:** Более точное определение пола, эмоций и усталости по голосу.

#### Что делаем:

1. **SpeechBrain** для определения пола и эмоций
   - Лёгкая модель (~50MB)
   - Работает на CPU

2. **auralis-vfs** для акустического анализа усталости
   - Монотонность, дрожание голоса, вялость
   - Объективные метрики

---

## Технологии

| Компонент | Инструмент | Назначение |
|-----------|-----------|------------|
| VAD | Silero VAD | Отсеивание тишины |
| Транскрипция | faster-whisper | Речь → текст |
| Diarization | Diart (pyannote) | Разделение спикеров |
| Анализ текста | Ollama (Llama 3) | Усталость, пол, перерыв |
| Пол/эмоции (голос) | SpeechBrain | Опционально |
| Усталость (голос) | auralis-vfs | Опционально |
| Оркестрация | C# BackgroundService | Управление пайплайном |
| Транспорт | RabbitMQ + SignalR | Передача данных |

## Файлы для изменения/создания

### Этап 1:
- `src/whisper-livekit/main.py` — замена на faster-whisper, добавление VAD, HTTP-эндпоинт
- `src/whisper-livekit/requirements.txt` — обновление зависимостей
- `src/whisper-livekit/Dockerfile` — возможно обновление
- `src/backend/.../Infrastructure/Services/WhisperService.cs` — **СОЗДАТЬ**
- `src/backend/.../Infrastructure/Services/OllamaService.cs` — добавить AnalyzeSpeakerAsync
- `src/backend/.../Presentation/Services/AiOrchestratorService.cs` — добавить анализ
- `src/backend/.../Presentation/Services/MeetingRecordingService.cs` — модифицировать PostProcessRecordingAsync
- `src/frontend/.../meeting/meeting.component.ts` — отображение анализа
- `src/frontend/.../core/services/signalr.service.ts` — событие SpeakerAnalysis

### Этап 2:
- `src/whisper-livekit/main.py` — добавить Diart
- `src/whisper-livekit/requirements.txt` — добавить pyannote.audio

### Этап 3:
- `src/whisper-livekit/` — добавить SpeechBrain, auralis-vfs
