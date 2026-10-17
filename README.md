# CoreLinq

![CI](https://github.com/KarpenkoDima/CoreLinq/actions/workflows/ci.yml/badge.svg)

Учебный проект про то, как устроен LINQ под капотом. Здесь три вещи, и каждая проверяет две другие:

- **руководство** «LINQ под капотом»: от `IEnumerable<T>` и `yield return` до `IQueryable`, EF Core и `IAsyncEnumerable`;
- **собственная реализация** `MyLinq`: итоговый код из Части 13 руководства;
- **тесты и эксперименты**: xUnit-тесты для `MyLinq` и испытания EF Core 10 на SQLite, которые проверяют утверждения руководства на настоящем провайдере.

## Что где лежит

| Путь | Что там |
|---|---|
| `docs/LINQ_Pod_Kapotom.md` | Руководство, 16 частей и глоссарий |
| `src/CoreLinq/MyLinq.cs` | `MyWhere`, `MySelect`, `MyOrderBy`/`MyThenBy`, `MyGroupBy`, `MyCount`, `MyAny`, `MyFirstOrDefault`, `MyToList` |
| `tests/CoreLinq.Tests` | Тесты `MyLinq` (net8.0 и net10.0) |
| `tests/CoreLinq.EfCore.Experiments` | Испытания EF Core 10 на SQLite в памяти (net10.0) |
| `.github/workflows/ci.yml` | CI: оба набора тестов на каждый push и pull request |

## Быстрый старт

Нужны .NET SDK 8.0 и 10.0: тесты `MyLinq` собираются под оба.

```bash
dotnet test tests/CoreLinq.Tests

# EF Core: с подробным логом, чтобы увидеть SQL, который печатают тесты
dotnet test tests/CoreLinq.EfCore.Experiments --logger "console;verbosity=detailed"
```

## Тесты MyLinq

| Файл | Что проверяет | Часть руководства |
|---|---|---|
| `GuideTests` | Боевые тесты 1–6: `GroupBy` + `Count`, три уровня `ThenBy`, вложенные вызовы, пустые и вырожденные данные, стабильность на 100 элементах | 12 |
| `FailFastTests` | Аргументы проверяются при вызове метода, а не при первом `MoveNext()` | 3 |
| `LazinessTests` | Что ленивое, что жадное, где работает ранний выход, оптимизация `ICollection<T>` | 2, 4, 6 |
| `BclParityTests` | На случайных данных `OrderBy`/`ThenBy`/`GroupBy` дают тот же результат, что стандартный LINQ (размеры 16 и 17 — граница между вставками и Introsort) | 10, 11 |
| `KnownLimitationsTests` | Упрощения, о которых честно сказано в руководстве: `null`-ключи в `GroupBy`, пересчёт ключей в `OrderBy` | 10, 11, 14 |
| `AsyncStreamTests` | `[EnumeratorCancellation]`, отмена через `WithCancellation`, async-LINQ, встроенный в .NET 10 | 14 |
| `GuideSyncTests` | Код в Части 13 руководства и `src/CoreLinq/MyLinq.cs` совпадают | 13 |

> **Правило синхронности.** `MyLinq.cs` — дословная копия блока кода из Части 13. Правишь одно — правь и другое.
> Если забыл, `GuideSyncTests` покраснеет.

## Испытания EF Core

Проект `tests/CoreLinq.EfCore.Experiments` берёт утверждения из Частей 7, 8, 14 и 15 и проверяет их на настоящем реляционном провайдере. SQLite в памяти выбран не случайно: он честно переводит LINQ в SQL и честно отказывается переводить то, чего не умеет. Провайдер `InMemory` так не делает, поэтому на нём «зелёный» тест ничего не доказывает (об этом говорит Часть 7).

| Файл | Что проверяет |
|---|---|
| `TranslationTests` | Литералы уходят в SQL константами, захваченные переменные — параметрами; свой метод в `Where` падает с `could not be translated`; `AsEnumerable`; финальный `Select`; `ToQueryString`, `LogTo` |
| `LoadingTests` | `ToList()` раньше времени, `IEnumerable` против `IQueryable` в сигнатуре, двойная итерация, ленивая загрузка и N+1, `Include`, проекция |
| `AsyncTests` | `ToListAsync` и `AsAsyncEnumerable` с токеном отмены |

Как это устроено:

- `TestDatabase` поднимает SQLite в памяти, создаёт схему и заливает пять пользователей с заказами. Данные одни и те же в каждом тесте, таблица описана в комментарии к классу.
- `CommandRecorder` — перехватчик `DbCommandInterceptor`. Он запоминает каждую команду, ушедшую в базу, вместе с параметрами. Поэтому тесты могут считать запросы (`database.Sent.Count`) и читать настоящий SQL.
- Каждый тест печатает SQL в лог через `ITestOutputHelper`.

Чтобы поставить свой опыт, скопируй любой тест, замени запрос и проверь `database.Sent`.

> Без `ORDER BY` SQL не гарантирует порядок строк, поэтому тесты, где порядок важен, сортируют явно.

## CI

Workflow `.github/workflows/ci.yml` запускается на каждый push в `main`, на каждый pull request и вручную. В нём два параллельных задания: тесты `MyLinq` на net8.0 и net10.0 и эксперименты с EF Core. Задание с EF Core запускается с `verbosity=detailed`, поэтому SQL видно прямо в логе прогона.

После тестов шаг `.github/scripts/test-summary.py` читает TRX-файлы и собирает сводку на странице прогона: таблицу итогов по каждому фреймворку и, под спойлером у каждого теста, всё, что тест вывел (в экспериментах с EF Core это SQL). Упавшие тесты дополнительно попадают в аннотации. Раннер зафиксирован на `ubuntu-24.04`, чтобы прогон не менялся сам по себе, когда `ubuntu-latest` переедет на новую версию.

## Лицензия

MIT, см. [LICENSE](LICENSE).
