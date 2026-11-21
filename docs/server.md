# BGSim.Server: симулятор как HTTP API

`src/BGSim.Server` — ASP.NET Core minimal API над библиотекой `src/BGSim`. Собирается в один бинарник Native AOT и
запускается в chiseled-образе (без оболочки и менеджера пакетов, не от root). Сервер ничего не сохраняет: тела запросов
не пишутся ни в журнал, ни на диск; в журнал попадают только предупреждения и ошибки.

## Запуск

На сервере (нужны Docker Engine 25+ и Compose v2):

```
git clone https://github.com/Manacost-Labs/BGSim && cd BGSim
GIT_SHA=$(git rev-parse HEAD) docker compose up -d --build
curl http://127.0.0.1:8080/health            # ok
```

`compose.yaml` публикует порт только на `127.0.0.1:8080` (другой порт — `BGSIM_PORT`), включает `read_only`,
`cap_drop: [ALL]`, `no-new-privileges`, лимиты 2 CPU и 256 МБ и `restart: unless-stopped`. Проверку здоровья делает сам
бинарник: `BGSim.Server --healthcheck` (GET `/health` на localhost, код выхода 0/1) — в chiseled-образе нет curl.

Без Docker: `dotnet run --project src/BGSim.Server -c Release` (JIT, порт из `ASPNETCORE_HTTP_PORTS`, по умолчанию 5000) или
`dotnet publish src/BGSim.Server -c Release -r linux-x64` (Native AOT; на Windows нужны MSVC Build Tools, `-r win-x64`).

### Снаружи: обратный прокси с TLS

Сервер говорит только HTTP. Наружу его выставляет прокси с TLS на той же машине, например Caddy (сертификат получает сам):

```
bgsim.example.org {
    request_body {
        max_size 64KB
    }
    reverse_proxy 127.0.0.1:8080
}
```

или nginx:

```
server {
    listen 443 ssl;
    server_name bgsim.example.org;
    # ssl_certificate ... (certbot)
    client_max_body_size 64k;
    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_read_timeout 10s;
    }
}
```

### Ключ API

Если задан `BGSIM_API_KEY`, все `/v1/*` требуют `X-API-Key: <ключ>` или `Authorization: Bearer <ключ>` (сравнение за
постоянное время); без ключа — 401. `/health` открыт всегда. Ключ кладите в `.env` рядом с `compose.yaml`
(`BGSIM_API_KEY=...`, файл не коммитить) или в переменные окружения сервиса.

### CORS

По умолчанию выключен. Для браузерных клиентов перечислите источники: `BGSIM_CORS_ORIGINS=https://site.example,https://other.example`
(методы GET и POST, заголовки `Content-Type`, `X-API-Key`, `Authorization`).

## Эндпоинты

Все ответы — JSON (`/health` — текст `ok`), с `Cache-Control: no-store` и `X-Content-Type-Options: nosniff`. Запросы —
`Content-Type: application/json`, иначе 415.

### `GET /health`

`200 ok` без работы — для Docker и балансировщика. Реестр эффектов и таблицы карт загружаются до того, как сервер начинает
слушать порт, поэтому ответ `/health` значит «готов».

### `GET /v1/info`

```json
{
  "version": "1.0.0",
  "commit": "41b9175e54053beda2c5bc063763c92cde014afe",
  "runtime": "native-aot",
  "cardDataPatch": "37.0.2.256589",
  "effects": 379,
  "effectProblems": 0,
  "limits": {
    "maxBodyBytes": 65536, "maxRuns": 10000, "defaultRuns": 10000, "maxTimeBudgetMs": 1000, "defaultTimeBudgetMs": 200,
    "defaultTargetHalfWidth": 0.01, "maxConcurrent": 2, "queueLimit": 4, "maxFightLogLines": 2000, "maxBoard": 7,
    "apiKeyRequired": false, "cors": false
  }
}
```

`commit` — `SourceRevisionId` сборки (в Docker — build arg `GIT_SHA`), `cardDataPatch` — патч таблиц карт
(`CombatRelevance.Patch`), `effects` — карт с эффектом в реестре (`EffectRegistry.Default.NormalCardIds`), `runtime` —
`native-aot` или `jit`.

### `POST /v1/simulate`

Шансы боя методом Монте-Карло со стороны `player`.

```json
{
  "player": {
    "health": 30, "tavernTier": 2,
    "board": [
      { "cardId": "BG31_149", "attack": 3, "health": 4 },
      { "cardId": "BG25_001", "attack": 2, "health": 1, "golden": true }
    ]
  },
  "opponent": {
    "health": 30, "tavernTier": 2,
    "board": [
      { "cardId": "BGS_004", "attack": 4, "health": 4 },
      { "cardId": "ZZ_NewCard_1", "attack": 2, "health": 2 }
    ]
  },
  "firstAttacker": "player",
  "options": { "runs": 10000, "timeBudgetMs": 200, "targetHalfWidth": 0.01, "damageCap": 0, "countDamage": true }
}
```

```json
{
  "win": 0.5029166666666667, "tie": 0.4970833333333333, "loss": 0,
  "runs": 9600, "halfWidth": 0.009999728794583603, "stoppedBy": "precision",
  "damageDealt": { "mean": 3, "low": 3, "high": 3, "max": 3, "counts": [0, 0, 0, 4828] },
  "lethalTaken": 0, "lethalDealt": 0, "damageCap": 0,
  "approximate": true, "unsimulated": 1,
  "unsimulatedCards": [ { "cardId": "ZZ_NewCard_1", "golden": false, "side": "opponent", "reason": "unknown", "extra": false } ],
  "estimated": 0, "truncated": 0, "elapsedMs": 96.725
}
```

| Поле | Смысл |
|---|---|
| `win`, `tie`, `loss` | Доли боёв; в сумме 1 |
| `runs`, `halfWidth` | Сколько боёв сыграно и 95 % полуширина (Вильсон) самой неуверенной из трёх долей |
| `stoppedBy` | `runs` (все заказанные), `precision` (достигнута `targetHalfWidth`), `time` (кончился `timeBudgetMs`), `cancelled` (клиент ушёл), `none` (пустая доска: считать нечего) |
| `damageTaken` / `damageDealt` | Урон по нашему герою в проигранных боях / по герою соперника в выигранных: среднее, 10-й и 90-й перцентили, максимум, число боёв на каждое значение урона. Нет поля — таких боёв не было или `countDamage: false` |
| `lethalTaken` / `lethalDealt` | Доля всех боёв, убивших героя; нет поля — здоровье героя не передано |
| `approximate`, `unsimulated`, `unsimulatedCards` | Результат приближённый: на досках есть существа или карты игрока, чьи боевые эффекты не моделируются. Причины — как в матрице покрытия: `missing_effect`, `missing_input`, `unknown` (карты нет в таблицах). `unsimulated` — счёт `CombatOdds.Unsimulated` (плюс один, если бои обрезал предел движка — `truncated`) |
| `estimated` | Число входов, переданных клиентом как оценка (`estimates`) |
| `elapsedMs` | Время симуляции на сервере |

Опции (`options`, все необязательны): `runs` 1…`BGSIM_MAX_RUNS` (по умолчанию `BGSIM_DEFAULT_RUNS`), `timeBudgetMs`
1…`BGSIM_MAX_TIME_BUDGET_MS` (по умолчанию 200), `targetHalfWidth` 0…0,5 (по умолчанию 0,01 = 1 п. п.; 0 — играть все
`runs`), `damageCap` 0…1000 (потолок урона лобби, 0 — нет), `countDamage` (по умолчанию true). Не меньше 500 боёв
(или все `runs`, если их меньше) играются всегда, предел времени проверяется только после них.

### `POST /v1/fight`

Один бой с журналом — для отладки. Вход тот же, без `options`, плюс необязательный `seed` (int). Без `seed` берётся сид
досок, так что одинаковые доски дают одинаковый бой.

```json
{
  "outcome": "win", "seed": 42, "truncated": false,
  "player": [ { "cardId": "BG31_149", "golden": false, "attack": 3, "health": 2 } ],
  "opponent": [],
  "log": [
    "our BG31_149 3/4 attacks their ZZ_NewCard_1 2/2",
    "their ZZ_NewCard_1 2/-1 takes 3 from our BG31_149 3/4",
    "...",
    "result: win"
  ],
  "logLines": 15, "logTruncated": false, "elapsedMs": 1.685
}
```

`player` / `opponent` — выжившие существа. Журнал обрезается на `BGSIM_FIGHT_LOG_LINES` строк (`logTruncated`,
`logLines` — сколько было).

### Ошибки

```json
{ "error": "invalid_request", "message": "player.board[0].attack: must be from 0 to 10000000",
  "errors": [ { "path": "player.board[0].attack", "message": "must be from 0 to 10000000" },
              { "path": "opponent.board[0].cardId", "message": "is required" } ] }
```

| Код | `error` | Когда |
|---|---|---|
| 400 | `invalid_json` | Не JSON, неизвестное поле, число в кавычках, комментарий, висячая запятая, повтор поля |
| 400 | `invalid_request` | Нарушен формат или диапазон; `errors` — до 20 мест с путём |
| 401 | `unauthorized` | Задан `BGSIM_API_KEY`, а ключа в запросе нет или он неверный |
| 413 | `body_too_large` | Тело больше `BGSIM_MAX_BODY_BYTES` |
| 415 | `unsupported_media_type` | `Content-Type` не JSON |
| 503 | `busy` | Заняты все слоты и очередь (или ожидание дольше `BGSIM_QUEUE_TIMEOUT_MS`); заголовок `Retry-After: 1` |
| 500 | `internal` | Исключение в симуляторе (в журнале — стек, без тела запроса) |

## Формат входа

Свой стабильный контракт, не внутренние record'ы библиотеки. Имена — camelCase. Все поля необязательны, кроме
`player`, `opponent` и у существа — `cardId`, `attack`, `health`.

**Бой:** `player`, `opponent` (стороны), `firstAttacker` (`"player"` / `"opponent"` / нет — по правилам игры: у кого
больше существ, при равенстве — случайно), `firstAttackerEntityId` (id существа, начавшего атаку), `lobbyTribes`
(племена лобби, для случайных призывов; нет — все).

**Сторона:**

| Поле | Что |
|---|---|
| `hero`, `heroPower` | Id карт героя и силы героя; сила героя считается картой игрока (как в трекере Hearthpulse) |
| `health` | Здоровье + броня героя (0…1000); без него нет оценки летала |
| `tavernTier`, `turn`, `playerId` | Уровень таверны (урон), ход (силы героя по ходам), PLAYER_ID лобби (порядок смертей) |
| `board` | До 7 существ слева направо |
| `extras` | Карты игрока: тринкеты, награды заданий, аномалия, божество, задания |
| `tags`, `extraTags` | Числовые теги игрока по имени из Power.log; теги карт игрока по id карты |
| `hand`, `secrets` | Существа в руке (до 10) и секреты (до 8) |
| `enchantments` | Зачарования на игроке: `{ "cardId", "scriptData1", "scriptData2", "attack", "health", "creatorEntityId" }` |
| `deathsBeforeCombat` | Существа, погибшие в начале боя до захвата, по порядку |
| `removedByExtras` | `{ "<id карты игрока>": [существа] }` — кого карта игрока убрала в начале боя |
| `deityAwakening` | `{ "progress", "total" }` — пробуждение божества |
| `estimates` | Оценки вместо невидимых значений: `{ "input", "value1", "value2", "basis", "tags" }` (имена входов — `SimInputs`) |
| `teammate` | Duos: доска напарника, входит в бой, когда эта опустела (без своего `teammate`) |

**Существо:**

| Поле | Что |
|---|---|
| `cardId`, `golden` | Id карты; золотой id (`TB_BaconUps_*`, `*_G`) сам даёт `golden` |
| `attack`, `health`, `maxHealth` | Текущие статы; `maxHealth` ≥ `health`, 0/нет — равно `health` |
| `taunt`, `divineShield`, `poisonous`, `venomous`, `reborn`, `windfury`, `megaWindfury`, `stealth` | Текущие ключевые слова; нет поля — как напечатано на карте, есть — ровно так |
| `tribes`, `tier`, `baseAttack`, `baseHealth`, `baseKeywords`, `mechanics` | Напечатанные данные; нет поля — из пула (см. ниже) |
| `effectMarkers` | Теги-маркеры эффектов из игры (`DEATHRATTLE`, `TRIGGER_VISUAL`, `AURA`, `AVENGE`, `OVERKILL`, `BACON_RALLY`) |
| `enchantments` | Зачарования на существе (как у игрока) |
| `scriptData1`…`scriptData4`, `entityId`, `creatorCardId`, `bloodGemAttack`, `bloodGemHealth`, `friendlyDeathsBeforeCombat`, `summonedInCombat` | Счётчики и связи, которые читают эффекты |
| `remembered`, `crew` | Убранные этим существом в начале боя; экипаж корабля Нелли (только у существ доски и руки) |

Имена племён, ключевых слов и механик — как в HearthstoneJSON (`BEAST`, `MECHANICAL`, `ALL`; `DIVINE_SHIELD`;
`DEATHRATTLE`, `BACON_RALLY`), регистр не важен. В `baseKeywords` и `mechanics` можно передать весь список `mechanics`
карты из HearthstoneJSON: незнакомые симулятору имена пропускаются.

### Дополнение напечатанных данных

Для существ пула (`MinionPool.g.cs`, сгенерирован из HearthstoneJSON и данных пула; патч — в `/v1/info`) сервер
дополняет отсутствующие поля: племена, уровень, механики, базовые ключевые слова (и по ним — текущие, если они не
переданы), `baseAttack` (с ним возвращается Reborn) и `baseHealth`; для золотой карты базовые статы ×2. Золотые id
приводятся к обычному через `CombatRelevance.Normalize`. Явные поля клиента всегда важнее. Пул хранит обычные версии
карт: Mega-Windfury золотой карты передавайте явно (`"megaWindfury": true`).

Без данных карты (не из пула) базовые ключевые слова — постоянные из текущих (Taunt, Poisonous, Windfury), как в трекере.

### «Не симулируется»

Флаг существа считается по правилу трекера Hearthpulse (`CombatRelevance.IsUnsimulated`): карта влияет на бой по
таблице значимости, а эффекта в `EffectRegistry` нет (или он требует руку соперника); маркер эффекта, которого карта не
печатает и которое не объясняет ни одно зачарование (Dark Gift и т. п.); зачарование, действующее в бою без эффекта.
Карта, которой нет в таблицах: если `effectMarkers` переданы — по ним, иначе сервер считает, что эффект может быть, и
помечает её `unknown`. Существа в руке не помечаются. Причины для ответа даёт `CombatSimulator.Explain`.

### Скрытая информация

Сервер считает ровно то, что прислал клиент, и ничего не додумывает о сопернике сверх таблиц карт. Эффекты, которым
нужна рука, для соперника не работают (`missing_input`). Оценки невидимых значений — дело клиента (`estimates`), их
число возвращается в `estimated`.

## Детерминизм

Один и тот же запрос даёт один и тот же ответ (кроме `elapsedMs`) на любой машине и при любой нагрузке, если
`stoppedBy` — `runs`, `precision` или `none`:

- сид берётся из самих досок: FNV-1a по статам, ключевым словам, id карт, золотости, племенам, счётчикам, зачарованиям,
  картам игрока, героям, размеру руки и первому атакующему (`CombatSimulator.Seed`; в Duos к нему подмешиваются доски
  напарников, `TeamSeed`);
- бои идут порциями по 50 (`ChunkRuns`), у каждой порции свой сид (SplitMix64 от сида досок и номера порции), итог
  суммируется строго по порядку порций, и остановка по точности проверяется после каждой порции в этом порядке;
- один запрос — один поток (`Parallelism = 1`), но и при нескольких потоках результат был бы тем же.

`stoppedBy: "time"` значит, что бои остановил `timeBudgetMs`: это начало той же последовательности, но число порций
зависит от скорости машины и нагрузки. Для воспроизводимого ответа задайте `runs` (не больше 500 — играются всегда) или
`timeBudgetMs` с запасом. `/v1/fight` детерминирован всегда: `System.Random(seed)`, без `seed` — сид досок.

## Лимиты и переменные окружения

| Переменная | По умолчанию | Что |
|---|---|---|
| `ASPNETCORE_HTTP_PORTS` | `8080` (в образе) | Порт |
| `BGSIM_API_KEY` | нет | Ключ для `/v1/*` |
| `BGSIM_CORS_ORIGINS` | нет (CORS выключен) | Разрешённые источники через запятую |
| `BGSIM_MAX_BODY_BYTES` | 65536 | Предел тела запроса (Kestrel), иначе 413 |
| `BGSIM_MAX_RUNS` / `BGSIM_DEFAULT_RUNS` | 10000 / 10000 | Предел и значение по умолчанию `options.runs` |
| `BGSIM_MAX_TIME_BUDGET_MS` / `BGSIM_DEFAULT_TIME_BUDGET_MS` | 1000 / 200 | Предел и значение по умолчанию `options.timeBudgetMs` |
| `BGSIM_DEFAULT_TARGET_HALF_WIDTH` | 0.01 | Остановка по точности по умолчанию (1 п. п.) |
| `BGSIM_MAX_CONCURRENT` | число ядер, доступных процессу | Симуляций одновременно (по потоку на каждую) |
| `BGSIM_QUEUE_LIMIT` | 2 × ядра | Сколько запросов ждут слота; сверх — 503 |
| `BGSIM_QUEUE_TIMEOUT_MS` | 5000 | Самое долгое ожидание в очереди; потом 503 |
| `BGSIM_FIGHT_LOG_LINES` | 2000 | Строк журнала в `/v1/fight` |
| `Logging__LogLevel__Default` | `Warning` | Уровень журнала (консоль) |

Неверное значение переменной — ошибка при старте, а не тихое значение по умолчанию. В контейнере с `cpus: "2"` .NET
видит 2 ядра, так что слоты и очередь подстраиваются под лимит сами.

Проверка входа (400): до 7 существ на сторону, руки до 10, карт игрока до 16, секретов до 8, зачарований до 32 на
существо или игрока, `remembered`/`crew` до 7 (только у существ доски и руки), погибших до 32, тегов до 128, карт в
`extraTags` до 16, оценок до 16, имён в списке до 32; id и имена тегов — 1–64 символа `A–Z a–z 0–9 _`; атака 0…10⁷,
здоровье 1…10⁷ (у погибших и убранных — любое в ±10⁷), уровень 0…7, здоровье героя 0…1000, ход 0…200,
`scriptData*` ±10⁷. Заголовки запроса — до 16 КБ, строка запроса — до 4 КБ.

## Ресурсы и замеры

Выбран Native AOT с `OptimizationPreference=Speed`, `InvariantGlobalization`, `UseSystemResourceKeys`, Workstation GC без
фонового режима (`ConcurrentGarbageCollection=false`), `EventSourceSupport=false`. Эффекты находятся отражением, поэтому
сборка `BGSim` целиком сохраняется от обрезки (`TrimmerRootAssembly`); число эффектов в AOT-сборке совпадает с JIT
(379 = 379, CI сверяет это на каждом прогоне), а ответы `/v1/simulate` и `/v1/fight` на 40 случайных досках из 272 карт пула
совпали побайтно (кроме `elapsedMs`).

Замер: `tools/BGSim.LoadTest` запускает сервер сам (новый процесс на каждый раунд), ждёт порт, шлёт доску 7×7 из
существ пула с хрипами, Avenge, Rally и божественными щитами (`board-7x7.json`) с фиксированными 2000 боёв на запрос,
затем 100 запросов разогрева, 200 запросов одним клиентом и 1000 запросов восемью клиентами. Память — после старта и
одного запроса (простой, 2 с) и после всех 1301 запроса.

```
dotnet publish src/BGSim.Server -c Release -r win-x64 -o pub/aot-speed                              # AOT, Speed
dotnet publish src/BGSim.Server -c Release -r win-x64 -o pub/aot-size -p:OptimizationPreference=Size
dotnet publish src/BGSim.Server -c Release -o pub/jit -p:PublishAot=false                            # JIT, framework-dependent
dotnet run --project tools/BGSim.LoadTest -c Release -- --server pub/aot-speed/BGSim.Server.exe --rounds 3
```

Машина: AMD Ryzen 9 5950X (16 ядер, 32 потока), 32 ГБ, Windows 11 Pro 26200; .NET SDK 10.0.302 (runtime 10.0.10);
Release; код — ветка `claude/server-docker` от `41b9175` (Sync from Hearthpulse 6656ea4c). Три раунда: медиана (мин.–макс.).

| | JIT (framework-dependent) | **AOT, Speed** | AOT, Size |
|---|---|---|---|
| Публикация | 0,93 МБ + общий рантайм ASP.NET Core | **12,7 МБ**, один файл | 9,7 МБ |
| Базовый образ (linux/amd64, сжатый) | `aspnet:10.0-noble-chiseled` 54,0 МБ | **`runtime-deps:10.0-noble-chiseled` 5,7 МБ** | то же |
| Старт до `/health` 200, мс | 285 (273–323) | **106 (105–141)** | 126 (115–158) |
| Старт до первого ответа `/v1/simulate`, мс | 538 (511–566) | **134 (133–172)** | 161 (150–197) |
| Рабочий набор в простое, МБ | 72,9 (72,6–72,9) | **37,2 (37,2–37,2)** | 39,1 (39,0–39,1) |
| Частная память в простое, МБ | 36,5 (36,1–36,5) | **23,6 (23,5–23,6)** | 25,9 (25,8–25,9) |
| Рабочий набор после 1301 запроса, МБ | 79,2 (79,0–79,4) | **43,5 (43,4–43,5)** | 45,3 (45,3–45,3) |
| Пиковый рабочий набор, МБ | 94,4 (94,0–94,4) | **47,7 (47,6–48,3)** | 50,1 (50,0–50,2) |
| Запросов/с, 1 клиент | 57,6 (56,9–57,9) | 41,5 (40,4–41,8) | 31,4 (30,1–32,6) |
| Медиана задержки, 1 клиент, мс | 15,5 (15,4–15,5) | 23,2 (23,1–23,7) | 30,4 (29,7–30,5) |
| Запросов/с, 8 клиентов | 313,7 (313,4–313,7) | 201,1 (189,5–201,2) | 159,1 (148,3–159,9) |
| CPU на запрос под нагрузкой, мс | 24,1 (24,1–24,3) | 37,9 (37,7–40,9) | 49,1 (48,3–51,8) |

Выбор: **AOT, Speed**. Вдвое меньше памяти, базовый образ в 9 раз меньше, готов к работе в 2,7 раза быстрее и не
разогревается (первый ответ через 134 мс против 538 мс у JIT). Цена — процессор: после разогрева JIT с динамическим PGO
играет бои примерно в 1,6 раза дешевле (24 против 38 мс CPU на запрос), так что для постоянно нагруженного многоядерного
сервера, где узкое место — CPU, а не память, выгоднее JIT-сборка (`-p:PublishAot=false` на образе
`aspnet:10.0-noble-chiseled`; в этом репозитории не собирается). Size против Speed: бинарник на 3 МБ меньше, но
медленнее на 21–24 % (CPU на запрос +30 %) и даже занимает больше памяти — не выбран. Проверка здоровья (`--healthcheck`) —
сырой сокет, а не `HttpClient`: с ним бинарник больше на 2,0 МБ (14,6 против 12,7 МБ, win-x64).

Linux-контейнер (CI, прогон 44923478777, коммит 6a39b52; `docker image inspect` и `docker stats`, 400 запросов
`/v1/simulate` с доской 7×7 по 4 одновременно):

| | x64 | arm64 |
|---|---|---|
| Размер образа (распакованный) | 24 МБ | 23 МБ |
| Память в простое | 26,5 МБ | 26,1 МБ |
| Память после 400 запросов | 34,1 МБ | 33,9 МБ |

CI печатает эти числа в сводку каждого прогона (job `image`). Локально Docker не проверялся.

## CI

`.github/workflows/docker.yml` на push, pull_request и вручную: `dotnet build -warnaserror` и `dotnet test` (без
`Category=Timing`), затем на нативных раннерах `ubuntu-24.04` и `ubuntu-24.04-arm` — `docker compose up --build --wait`,
запросы к `/health`, `/v1/info` (runtime, commit, число эффектов = JIT), `/v1/simulate`, `/v1/fight`, два ответа 400,
размер образа и память контейнера. Образ никуда не публикуется. Права — `contents: read`, actions закреплены по SHA,
обновляет их Dependabot (`.github/dependabot.yml`).

Публикация AOT идёт без предупреждений обрезки. Отражение в `EffectRegistry` и `CombatEffect` (поиск классов эффектов
и их хуков) держится на `TrimmerRootAssembly`; его предупреждения IL2026, IL2070 и IL2072 подавлены на месте с
обоснованием в Hearthpulse, откуда приходит код библиотеки (Hearthpulse 19fcf95c, BGSim df7a2e1). Новая рефлексия в
симуляторе там же должна оставаться AOT-совместимой (`tools/bgsim/README.md` в Hearthpulse).
