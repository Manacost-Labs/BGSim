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

## Сборка и тесты

```
dotnet build BGSim.slnx
dotnet test BGSim.slnx
```

Нужен .NET SDK 10.

## Лицензия

Проприетарная, все права защищены — см. [LICENSE](LICENSE).
