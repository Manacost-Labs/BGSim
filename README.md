# BGSim

Симулятор боя Hearthstone Battlegrounds из [Hearthpulse](https://github.com/Manacost-Labs): по двум доскам считает
шансы победы, ничьей и поражения методом Монте-Карло, а также урон по героям и шанс летального урона. Библиотека на
.NET 10 без внешних зависимостей.

## Состав

| Путь | Что там |
|---|---|
| `src/BGSim/CombatSimulator.cs` | Вход: `CombatSimulator.Run` (шансы), `RunSingle` (один бой с журналом), `Explain` (что на доске не симулируется и почему); `SimMinion`, `SimSide`, `SimOptions`, `CombatOdds` |
| `src/BGSim/Simulation/` | Движок боя: атаки, цели, урон, смерти, статы (`Fight*.cs`), стороны и существа (`Side`, `Unit`), племена и ключевые слова (`Traits`) |
| `src/BGSim/Effects/` | Эффекты карт по семействам (`attack`, `deaths`, `ongoing`, `player`), реестр `EffectRegistry`, таблица значимости карт для боя (`CombatRelevance`) и покрытие (`SimCoverage`). Как добавить эффект — [Effects/README.md](src/BGSim/Effects/README.md) |
| `src/BGSim/CombatDamage.cs` | Урон по герою после боя |
| `src/BGSim/Cards/Card.cs` | Минимальный контракт данных карт (`Card`, `CardDb`) для карт, которых нет в таблице значимости |
| `docs/bg-sim-matrix.md` | Матрица покрытия: статус каждого существа пула, силы героя, тринкета и тёмного дара |
| `tests/BGSim.Tests/` | Тесты движка, эффектов, точности и свойств (xunit, CsCheck) |
| `src/BGSim.Server/` | HTTP API симулятора (ASP.NET Core minimal API, Native AOT), `Dockerfile` и `compose.yaml` в корне — [docs/server.md](docs/server.md) |
| `tests/BGSim.Server.Tests/` | Тесты API: проверка входа, лимиты, ключ, дополнение данных из пула, детерминизм |
| `tools/BGSim.LoadTest/` | Замер сервера: старт, память, запросов/с (вне образа) |

`src/BGSim/` и `tests/BGSim.Tests/` — копия из Hearthpulse, которую синхронизация перезаписывает целиком; свои файлы BGSim
там — только оба `.csproj`, `Cards/Card.cs` и `Shims/`. Сервер, его тесты и инструменты живут вне этих папок.

Пространства имён совпадают с Hearthpulse (`Hearthpulse.Core.Battlegrounds`, `...Effects`, `...Simulation`), чтобы
файлы переносились между репозиториями без правок.

## Пример

```csharp
using Hearthpulse.Core.Battlegrounds;

var ours = new[]
{
    SimMinion.Create("BG31_149", 3, 2, Tribe.Naga),                                     // Bubble Gunner
    SimMinion.Create("BG31_815", 2, 4, keywords: Keyword.Taunt | Keyword.DivineShield), // Dune Dweller
};
var theirs = new[] { SimMinion.Create("BGS_004", 4, 4, Tribe.Demon) };                   // Wrath Weaver

var odds = CombatSimulator.Run(ours, theirs, firstAttackerIsOurs: null);
Console.WriteLine($"Победа {odds.Win:P0}, ничья {odds.Tie:P0}, поражение {odds.Loss:P0}");
```

`firstAttackerIsOurs: null` — первый атакующий определяется по правилам игры (больше существ, при равенстве — случайно).
Число прогонов, предел времени, отмена и предел урона задаются через `SimOptions` в перегрузке
`Run(SimSide, SimSide, bool?, SimOptions?)`.

## Сервер (HTTP API, Docker)

`src/BGSim.Server` — тот же симулятор как HTTP API: `POST /v1/simulate` (шансы боя), `POST /v1/fight` (один бой с
журналом), `GET /v1/info`, `GET /health`. Один бинарник Native AOT в chiseled-образе (на Windows-замере — около 40 МБ
рабочего набора и ~0,1 с до готовности); ничего не хранит и не пишет в журнал тела запросов. Поднять на сервере:

```
GIT_SHA=$(git rev-parse HEAD) docker compose up -d --build
```

Эндпоинты, формат запросов, лимиты, переменные окружения, обратный прокси с TLS и замеры ресурсов —
[docs/server.md](docs/server.md).

## Сборка и тесты

```
dotnet build BGSim.slnx
dotnet test BGSim.slnx
```

Нужен .NET SDK 10.

## Лицензия

Проприетарная, все права защищены — см. [LICENSE](LICENSE).
