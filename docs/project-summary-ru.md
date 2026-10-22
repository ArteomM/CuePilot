# CuePilot — подробный summary проекта (RU)

## 1. Что это за проект

`CuePilot` — это desktop AI-суфлер для встреч и звонков, ориентированный на русскоязычные сценарии.  
Ключевая идея продукта: помогать пользователю в реальном времени во время разговора **без добавления meeting-бота** в созвон.

Базовый контур продукта:

- захват `system audio` + `mic` локально на устройстве;
- потоковая обработка речи (STT);
- сбор контекста диалога;
- генерация коротких подсказок;
- показ в desktop UI/overlay.

Целевой UX:

- минимум шума в интерфейсе;
- быстрые, короткие и релевантные подсказки;
- прозрачный контроль приватности;
- предсказуемое поведение при screen-share сценариях.

---

## 2. Продуктовые цели и гипотезы

### 2.1 Главная гипотеза MVP

Если дать русскоязычному пользователю локальный, быстрый и удобный desktop-суфлер, который:

- работает без bot-join,
- стабильно берет системный звук и микрофон,
- выдает короткие подсказки в реальном времени,

то это даст измеримый прирост качества ответов и уверенности в разговоре.

### 2.2 Цели MVP

1. **Надежный audio capture** на Windows (`system + mic`).
2. **Рабочий STT pipeline** с локальной транскрибацией через `whisper.cpp`.
3. **Desktop shell** с Start/Stop, diagnostics, режимами окна.
4. **Базовый overlay** (mini, always-on-top) и transcript drawer.
5. **Базовые метрики и проверяемость** (soak-test + report).

### 2.3 Что не в MVP

- CRM и внешние интеграции,
- full meeting automation,
- тяжелая multi-agent оркестрация,
- enterprise-настройки и расширенный backend.

---

## 3. Текущая архитектурная стратегия

Зафиксировано ADR:

- стек десктопа на production-траектории: `Tauri 2 + Rust + React/TS`;
- платформенная стратегия: `Windows-first`, далее `macOS`.

Фактический текущий рабочий инкремент реализован как быстрый bootstrap:

- `apps/DesktopCapture` на .NET для реального системного захвата и STT-проверок;
- `apps/CuePilotDesktop` на WPF как UI shell для ускоренного UX/flow тестирования.

Это инженерно валидный этап `research + shell`, который не противоречит целевой архитектуре, а снижает риск по core-loop.

---

## 4. Что уже реализовано (факт по коду)

## 4.1 Audio/STT backend (`apps/DesktopCapture`)

Реализовано:

- захват `system audio` через WASAPI loopback;
- захват `mic` через WASAPI capture endpoint;
- одновременная обработка двух потоков;
- live stats:
  - `system.avg/system.peak`,
  - `mic.avg/mic.peak`;
- chunking аудио по времени;
- ресемплинг в `16kHz mono` для STT;
- интеграция `whisper.cpp` через CLI;
- отдельные STT-результаты по источникам:
  - `[stt][system] ...`
  - `[stt][mic] ...`;
- soak-тест режим (`--soak-minutes`);
- safety checks output endpoint state:
  - device id,
  - volume/mute,
  - mix format (`sample-rate/channels/bits`) до/после;
- формальный JSON-отчет по прогону (`--soak-report`).

Текущее состояние сборки:

- target обновлен на `net8.0-windows`;
- warning по EOL `netcoreapp2.1` устранен.

## 4.2 Desktop UI shell (`apps/CuePilotDesktop`)

Реализовано:

- Home-экран со списком сессий и drill-down в выбранную сессию;
- screen выбранной сессии с вкладками `Summary / Transcript / Usage`;
- изоляция контекста: summary/transcript строго в рамках выбранной сессии;
- mini overlay + indicator bar для live-работы поверх окон;
- `What should I say` для генерации следующей реплики по текущей сессии;
- `Smart` режим (extra reasoning before answering);
- actions в сессии: `Follow-up email`, `Regenerate`, `Copy summary`;
- настройки LLM в UI (`OpenAI API key`, model, base URL);
- горячие клавиши уровня приложения:
  - `Ctrl+\` — show/hide overlay,
  - `Ctrl+Enter` — ask,
  - `Ctrl+R` — clear chat,
  - `Ctrl+Shift+\` — start/stop session,
  - `Ctrl+Arrow` — move widget,
  - `Ctrl+Shift+Up/Down` — scroll/chat navigation.

### Выделенные слои в UI

Добавлены отдельные сервисы/модели:

- `CaptureOrchestratorService` — управление backend-процессом + события;
- `EnvironmentDiagnosticsService` — проверки readiness;
- `DiagnosticsViewModel` — состояние diagnostics для UI;
- `OverlayPositionStore` — сохранение позиции overlay.
- `SessionLifecycleService` + `JsonSessionStore` — lifecycle и персист сессий.
- `ConversationAssistService` — orchestration генерации подсказок/summary.
- `OpenAiAssistClient` — HTTP-клиент вызова LLM API.

---

## 4.3 LLM pipeline в текущей реализации

LLM используется как движок генерации поверх контекста выбранной сессии.

Цепочка:

`UI -> MainViewModel -> ConversationAssistService -> OpenAiAssistClient -> LLM -> MainViewModel -> UI`

Техническая цепочка Assist (button/hotkey, единый path):

`Assist Button/Hotkey -> UI Command Handler -> MainViewModel -> ConversationAssistService -> BuildTranscriptContext(session) -> Load AppSettings (apiKey/model/baseUrl/smartMode) -> Prompt Select (normal/smart) -> OpenAiAssistClient -> Validate/Postprocess -> JsonSessionStore -> UI`

Ключевые принципы:

- одна активная сессия и один контекст;
- без смешивания данных между сессиями;
- normal mode: быстрый ответ с минимальным reasoning;
- Smart mode: расширенный контекст + extra reasoning before answering;
- ошибки API (ключ/модель/baseUrl/сеть/таймаут) показываются пользователю, без silent fail;
- кнопка и hotkey используют один и тот же handler без расхождений в логике;
- повторный конфликтующий Assist-вызов блокируется, пока выполняется текущий запрос;
- результат генерации сохраняется в session store (summary/suggestions/usage), transcript сохраняется как история.

Реализованный слой подготовки transcript для Summary:

- раздельный capture источников `SYSTEM`/`MIC` сохраняется отдельно;
- добавлены уровни текста: `raw -> cleaned(FormKC) -> formatted(SPEAKER: text) -> summary-ready`;
- `summary-ready` исключает служебные префиксы `SYSTEM:`/`MIC:` и нормализует Unicode/пробелы;
- для summary-контекста убираются соседние дубли из промежуточных ASR-проходов.

Настройки LLM:

- `OpenAiApiKey`
- `OpenAiModel`
- `OpenAiBaseUrl`
- `SmartModeEnabled`

хранятся в `AppSettings` через `JsonSettingsService`.

Формальный контракт описан в:

- `docs/architecture/llm-session-pipeline-contract.md`

---

## 4.4 Контракт ускоренной и качественной транскрибации

Целевой поток обработки аудио зафиксирован как:

`Audio -> Normalization -> Noise Reduction -> VAD -> Chunking -> Acoustic Model (Streaming) -> Decoder -> Beam Search + LM rescoring -> Punctuation/Capitalization -> (optional) Diarization -> Text Normalization -> Output`

Рекомендованные техники:

- предобработка сигнала:
  - RMS-нормализация громкости;
  - шумоподавление (spectral subtraction / Wiener / neural denoiser);
  - ресэмплинг в `16kHz` (для моделей, обученных на этом диапазоне);
- VAD:
  - локальный `WebRTC VAD` или `Silero VAD`;
  - обрезка пауз/тишины для снижения нагрузки;
- кадрирование:
  - окно `20ms`, шаг `10ms` как baseline;
- акустическая модель:
  - streaming-inference (`Whisper` / `Conformer` / эквиваленты);
  - допускается quantization/pruning при контроле деградации качества;
- декодирование:
  - beam width `5-10` как дефолтный баланс;
  - CTC-prefix beam search для CTC-подобных выходов;
  - optional LM rescoring;
- пост-обработка:
  - восстановление пунктуации/регистра;
  - нормализация чисел/дат/сокращений;
  - optional spellcheck;
- optional diarization:
  - при необходимости маркировки спикеров после VAD;
- оптимизация:
  - GPU batching;
  - ONNX/TensorRT;
  - асинхронный pipeline с очередями между стадиями;
- адаптация к домену:
  - fine-tuning на целевом корпусе;
  - bias-lexicon для имен и терминов.

Контроль качества:

- WER-мониторинг на валидационном наборе;
- confidence scoring per-token/per-phrase;
- целевая задержка для live UX: субсекундные инкрементальные обновления.

---

## 4.5 Режимы интерфейса и правила использования

Зафиксированные режимы и правила:

- `SmartMode`:
  - переключает normal/smart шаблоны в Assist pipeline;
  - состояние хранится в `AppSettings` и применяется без перезапуска;
- `Transcript`:
  - ответы строятся по транскрипту выбранной сессии;
  - при превышении лимита токенов применяется retrieval/компрессия;
- `Summary`:
  - генерация через выделенный Summary pipeline;
  - контекст только `summary-ready transcript`;
  - вывод в структурированном формате (`Action Items`, `Conversation Highlights`);
- `Screen`:
  - приоритет контекста текущего UI (OCR/UI-tree/active element);
  - fallback на transcript при пустом screen context;
- overlay actions:
  - `What should I say` -> короткая реплика (до 2-3 предложений);
  - `Follow-up questions` -> список 3-5 вопросов без ответов;
  - `Recap` -> краткое резюме (до 70 слов).

Общие правила для всех режимов:

- строгая изоляция выбранной сессии;
- единый path кнопок и hotkeys через один command handler;
- результат каждой генерации сохраняется в `JsonSessionStore` (history/usage/summary по типу операции);
- переключение режима должно менять контекст последующих вызовов без перезапуска.

---

## 5. Текущая точка проекта (где мы находимся)

Проект находится в фазе:

**`Sprint 0 -> Sprint 1 transition`** (по факту реализации).

То есть:

- core audio/STT контур подтвержден в рабочем прототипе;
- desktop shell и базовые UX-механики уже живые;
- архитектура начала отделяться по сервисам и VM;
- можно переходить от spike-логики к системному укреплению и расширению UI/flow.

Иными словами:

**мы уже не на этапе “идея/документы”, а на этапе “реально работающий MVP-каркас + интеграция подсистем”.**

---

## 6. Roadmap (актуализированная)

## Этап A — Stabilize Foundation (текущий следующий фокус)

Цель: сделать текущий контур надежным, поддерживаемым и удобным для разработки.

Задачи:

1. Довести `MainWindow` до более чистого MVVM (команды/состояния).
2. Убрать технический долг в `DesktopCapture`:
   - типизация/nullable,
   - структурированные события вместо текст-парсинга.
3. Добавить устойчивые app-level логи и crash capture для UI.
4. Формализовать настройки (hotkeys, chunkSeconds, language, пути моделей).

Результат этапа:

- стабильный baseline для командной разработки и расширения.

## Этап B — UX Core Loop

Цель: сделать продуктовый цикл “слышу -> понимаю -> подсказываю” более полезным в реальном времени.

Задачи:

1. Улучшить transcript drawer:
   - фильтры (`all/system/mic`),
   - поиск,
   - копирование фрагментов.
2. Подключить базовый suggestion engine v0:
   - короткий one-liner,
   - next best question.
3. Привязать режимы (`Meeting/Sales/Interview`) к разным prompt-шаблонам.

Результат этапа:

- пользователь начинает получать практическую ценность прямо в UI.

## Этап C — Reliability & Validation

Цель: подготовка к закрытой бете.

Задачи:

1. Автоматизировать regression для capture/STT/overlay.
2. Расширить soak reporting и compatibility matrix.
3. Ввести release gate checklist на каждый beta билд.

Результат этапа:

- управляемый quality loop и предсказуемые релизы.

## Этап D — Product Hardening / Beta

Цель: проверить market-fit на реальных пользователях.

Задачи:

1. Cohort beta (20–30 пользователей целевых ролей).
2. Триаж проблем по latency/relevance/UX friction.
3. Приоритизация улучшений на основе метрик, а не субъективных мнений.

Результат этапа:

- подтверждение/опровержение ключевой гипотезы MVP.

---

## 7. KPI и критерии успеха

Ключевые KPI:

- `TimeToFirstSuggestion`,
- `TranscriptLag`,
- `SuggestionAcceptanceRate`,
- `STTCorrectionRate`,
- `SessionCrashFreeRate`,
- `ScreenContextHitRate` (на соответствующем этапе).

Минимальные критерии готовности к beta:

- стабильный capture (`system + mic`) без регрессий;
- предсказуемый soak PASS в целевых сценариях;
- рабочий overlay + transcript UX;
- отсутствие критических проблем по приватности/управляемости.

---

## 8. Риски и текущие ограничения

### Технические риски

- разные устройства и драйверы аудио на Windows;
- качество STT на шуме/перекрытиях;
- долговременная стабильность процесса и потоков.

### UX-риски

- перегрузка UI и отвлечение пользователя;
- неоднозначность “когда показывать подсказку”.

### Архитектурные ограничения сейчас

- часть интеграции все еще держится на текстовом парсинге логов;
- shell работает как MVP-слой, не финальная production-архитектура.

---

## 9. Что рекомендуется делать следующим шагом (конкретно)

Приоритетный следующий пакет работ:

1. **MVVM uplift**:
   - вынести UI state и команды из `MainWindow` в `MainViewModel`.
2. **Capture contracts**:
   - заменить текстовые логи на структурированный event channel.
3. **Transcript UX**:
   - фильтры, поиск, ограничения буфера и экспорт.
4. **Settings persistence**:
   - hotkeys/mode/chunkSeconds/language/model path.
5. **Quality gates**:
   - smoke test сценарии для каждого nightly/build.

---

## 10. Краткий executive summary

`CuePilot` уже прошел критическую точку “проект только на бумаге”.  
Сейчас есть рабочий end-to-end контур:

- захват аудио,
- локальная транскрибация,
- desktop shell,
- diagnostics,
- overlay,
- transcript drawer,
- базовые механизмы качества (soak/report).

Текущая стадия — **укрепление и продуктовая доводка** перед beta-валидацией.  
Фокус ближайшего этапа: сделать текущую реализацию более системной, управляемой и готовой к пользовательским тестам.
