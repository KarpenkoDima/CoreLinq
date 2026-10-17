# 🎓 Методичка: LINQ под капотом — от `IEnumerable` до `IQueryable`

> **Наставление.** Я в разработке больше десяти лет. То, что ты сейчас читаешь — не просто справочник.  
> Это сухой остаток сотен часов разбора чужих багов, код-ревью и собеседований.  
> LINQ — это не просто методы. Это комбинация итераторов, делегатов, конечных автоматов и деревьев выражений.  
> Понимание этой механики отделяет хорошего разработчика от посредственного.

---

## 🗺️ Карта методички

Читай последовательно или прыгай сразу на свой уровень.

| Часть | Тема | Уровень |
|---|---|---|
| 1–3 | `IEnumerable`, вариантность, `yield return`, Fail Fast | 🟢 Middle |
| 4–6 | Реализация методов, классификация, конвейер + схема pipeline | 🟢 Middle |
| 7 | `IQueryable`, деревья выражений, ошибки трансляции | 🟡 Senior |
| 8 | Золотые правила и антипаттерны | 🟢 Middle |
| 9 | Аллокации, GC, closures | 🟡 Senior |
| 10–13 | `ThenBy`, `GroupBy`, боевые тесты, итоговый код | 🟡 Senior |
| 14 | Под капотом .NET: `Lookup`, кэш ключей и частичная сортировка, быстрые пути и SIMD, `IAsyncEnumerable` | 🟡 Senior |
| 15 | Реальные баги: двойная итерация, N+1, бесконечный итератор + чеклист | 🟡 Senior |
| 16 | Zero-Allocation LINQ на `ref struct` | 🔴 Hardcore |
| Приложение | Глоссарий терминов | — |

> 🔧 **Версии и проверка.** Код на LINQ to Objects скомпилирован и запущен на .NET 8 и .NET 10 (C# 12–14). Примеры с EF Core сверены с документацией Microsoft, но здесь не запускались. Всё, что зависит от версии .NET или C#, помечено прямо в тексте.

---

## 🏗️ Часть 1. Фундамент: `IEnumerable<T>` и методы расширения

### Базовый контракт

`IEnumerable<T>` — это интерфейс, который обещает одно: коллекцию **можно перебрать**. Он не хранит данные, не знает их количество. Он лишь предоставляет перечислитель через метод `GetEnumerator()`.

```csharp
// Контракт интерфейса (упрощённо) — обрати внимание на 'out'
public interface IEnumerable<out T>  // ← ковариантный параметр типа
{
    IEnumerator<T> GetEnumerator();
}

// Контракт перечислителя (упрощённо: MoveNext и Reset на самом деле объявлены в базовом IEnumerator)
public interface IEnumerator<out T> : IDisposable  // ← тоже ковариантный
{
    T Current { get; }       // Текущий элемент
    bool MoveNext();         // Перейти к следующему. Вернуть false, если коллекция кончилась
    void Reset();            // Legacy-метод. В реальных итераторах почти всегда
                             // бросает NotSupportedException. В коде не используй.
}
```

Обрати внимание на `IDisposable`: `foreach` всегда вызывает `Dispose()` у перечислителя — даже если цикл прервали через `break` или исключение. Благодаря этому у итераторов отрабатывают `finally`-блоки и освобождаются ресурсы.

Именно на этом фундаменте стоит весь LINQ. Всё остальное — надстройка.

### Ковариантность `out T` — почему это важно

Ключевое слово `out` перед `T` означает **ковариантность**: интерфейс можно использовать с более производным типом там, где ожидается базовый.

```csharp
IEnumerable<string> strings = new List<string> { "a", "b" };

// Работает — потому что IEnumerable<out T> ковариантен
IEnumerable<object> objects = strings;  // ✅ string → object

// НЕ работает с инвариантными коллекциями
List<string> list = new List<string>();
List<object> objs = list;               // ❌ ошибка компиляции — List<T> инвариантен
```

Именно благодаря `out T` методы LINQ так легко принимают `IEnumerable<string>` там, где ожидается `IEnumerable<object>`.

Логика простая: если коллекция умеет **выдавать** `string`, она точно умеет выдавать `object` — ведь `string` является `object`. Обратное невозможно: получить `string` из коллекции `object` небезопасно.

> ⚠️ **Работает только для ссылочных типов.** `IEnumerable<int>` в `IEnumerable<object>` не превратится: каждый `int` пришлось бы упаковать (boxing), а ковариантность — это просто приведение ссылки, без преобразования данных.

Та же идея, но в обратную сторону — **контравариантность** (`in T`):

```csharp
// IComparer<in T> контравариантен
IComparer<object> objectComparer = Comparer<object>.Create((x, y) => 0);
IComparer<string> stringComparer = objectComparer; // ✅ Работает!
```

Если компаратор умеет **принимать** любой `object`, он точно справится со `string`. Здесь всё наоборот: тип течёт не вниз по иерархии, а вверх.

| | Ключевое слово | Приведение типов | Пример |
|---|---|---|---|
| Инвариантность | *(нет)* | Запрещено | `IList<string>` ≠ `IList<object>` |
| Ковариантность | `out T` | Разрешено в сторону базового | `IEnumerable<string>` → `IEnumerable<object>` |
| Контравариантность | `in T` | Разрешено в сторону производного | `IComparer<object>` → `IComparer<string>` |

### Методы расширения: как LINQ «прикрепляется» к коллекциям

Все методы LINQ (`.Where`, `.Select`, `.ToList`...) отсутствуют в самом интерфейсе `IEnumerable<T>`. Они добавляются через **методы расширения** — статические методы с ключевым словом `this` перед первым параметром.

```csharp
// Синтаксис метода расширения
public static class MyLinq
{
    // Ключевое слово 'this' — вот что делает метод расширением
    public static IEnumerable<T> MyWhere<T>(this IEnumerable<T> items, Func<T, bool> predicate)
    {
        // ...
    }
}

// Теперь можно вызывать так, будто метод встроен в сам интерфейс:
var result = myList.MyWhere(x => x > 5);
```

> ⚠️ **Типичная ошибка:** написать сигнатуру без `this`. Без него компилятор не считает метод расширением — его придется вызывать как обычный статический: `MyLinq.MyWhere(myList, ...)`. Разница на первый взгляд мелкая, а по сути — архитектурная.

---

## 🌀 Часть 2. Ленивые вычисления и `yield return`

### Что такое отложенное выполнение

Когда ты пишешь `var query = items.Where(x => x > 5)`, **ничего не происходит**. Никакой фильтрации. В памяти создаётся только объект-итератор, который описывает *что нужно сделать*. Реальная работа начнётся только тогда, когда кто-то вызовет `MoveNext()` — например, через цикл `foreach`.

```csharp
var query = items.MyWhere(x => x > 5); // <- тут тихо. Итератор создан, но не запущен.

foreach (var item in query)            // <- вот тут всё оживает. MoveNext() вызывается
{                                      //    для каждого элемента по одному.
    Console.WriteLine(item);
}
```

### `yield return` — сердце ленивого итератора

Ключевое слово `yield return` превращает метод в **конечный автомат** (state machine). Компилятор сгенерирует скрытый класс, который будет помнить, на каком элементе метод «заснул», и «просыпаться» при следующем вызове `MoveNext()`.

```csharp
private static IEnumerable<T> WhereIterator<T>(IEnumerable<T> items, Func<T, bool> predicate)
{
    foreach (var item in items)
    {
        if (predicate(item))
            yield return item; // Метод "засыпает" здесь. Управление уходит вызывающему коду.
                               // При следующем MoveNext() выполнение продолжится отсюда.
    }
}
```

**Что это даёт?** Если нам из миллиона элементов нужен только первый подходящий — мы не будем обходить остальные 999 999. Экономия памяти и процессорного времени.

---

## 🛡️ Часть 3. Принцип Fail Fast и паттерн разделения методов

### Проблема: `yield return` ломает проверку аргументов

Из-за того, что метод с `yield return` компилятор превращает в конечный автомат, **его тело вообще не начнёт выполняться** в момент вызова. Код запустится только при первом `MoveNext()` — например, когда стартует `foreach`.

Это значит, что вот такая «проверка» бесполезна:

```csharp
// ❌ НЕПРАВИЛЬНО — проверка не сработает сразу!
public static IEnumerable<T> MyWhere<T>(this IEnumerable<T> items, Func<T, bool> predicate)
{
    if (items == null) throw new ArgumentNullException(nameof(items)); // Эта строка отложена!

    foreach (var item in items)
        if (predicate(item)) yield return item;
}
```

Если передать `null`, ошибка выстрелит не там, где вызвали `MyWhere`, а где-то в глубине цикла `foreach`. Это нарушение принципа **Fail Fast** — «падай быстро, падай громко, падай в нужном месте».

### Решение 1: два метода (классический подход до C# 7.0)

Разделяем «немедленную» логику (валидацию) и «отложенную» (итерацию) по разным методам:

```csharp
public static class MyLinq
{
    // Публичный метод — ТОЛЬКО валидация, НИКАКОГО yield return
    // Выполнится синхронно и мгновенно при вызове
    public static IEnumerable<T> MyWhere<T>(this IEnumerable<T> items, Func<T, bool> predicate)
    {
        if (items == null)     throw new ArgumentNullException(nameof(items));
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));

        return WhereIterator(items, predicate); // Делегируем в приватный итератор
    }

    // Приватный итератор — ВСЯ ленивая логика здесь
    // Компилятор превратит ТОЛЬКО этот метод в конечный автомат
    private static IEnumerable<T> WhereIterator<T>(IEnumerable<T> items, Func<T, bool> predicate)
    {
        foreach (var item in items)
            if (predicate(item)) yield return item;
    }
}
```

### Решение 2: локальные функции (современный C# 7.0+)

Начиная с C# 7.0 тот же принцип реализуется чище — через **локальную функцию**. Приватный метод не засоряет класс, логика остаётся в одном месте:

```csharp
public static IEnumerable<T> MyWhere<T>(this IEnumerable<T> items, Func<T, bool> predicate)
{
    // Современная валидация — бросает ArgumentNullException с именем параметра автоматически
    ArgumentNullException.ThrowIfNull(items);
    ArgumentNullException.ThrowIfNull(predicate);

    return Iterator(); // Вызываем локальную функцию — она и есть конечный автомат

    // Локальная функция: видит параметры родителя, но компилятор превращает
    // ТОЛЬКО её в конечный автомат. Публичный метод остаётся «немедленным».
    IEnumerable<T> Iterator()
    {
        foreach (var item in items)
            if (predicate(item)) yield return item;
    }
}
```

Оба подхода решают одну проблему. Локальные функции — предпочтительный выбор в новом коде: меньше шума в классе, лучше читаемость.

Стандартный LINQ устроен по тому же принципу «привратник + итератор»: публичный метод сразу проверяет аргументы, а саму работу отдаёт отдельному классу-итератору или приватному `static`-методу. Локальные функции в самом BCL встречаются реже — но в прикладном коде это самый аккуратный вариант.

> ✅ **Pull Request одобрен.** Публичный метод — привратник. Итератор (приватный или локальный) — рабочая лошадь.

---

## ⚙️ Часть 4. Реализация ключевых методов

### `MySelect` — проекция (преобразование типа)

`Select` отличается от `Where` тем, что может **изменить тип данных**: из `IEnumerable<User>` получить `IEnumerable<string>`. Для этого нужны два generic-параметра.

```csharp
// T — тип входных элементов, R — тип результирующих элементов
public static IEnumerable<R> MySelect<T, R>(this IEnumerable<T> items, Func<T, R> map)
{
    if (items == null) throw new ArgumentNullException(nameof(items));
    if (map == null)   throw new ArgumentNullException(nameof(map));

    return SelectIterator(items, map);
}

private static IEnumerable<R> SelectIterator<T, R>(IEnumerable<T> items, Func<T, R> map)
{
    foreach (var item in items)
        yield return map(item); // Применяем преобразование к каждому элементу
}
```

### `MyToList` — материализация (немедленное выполнение)

Это метод **немедленного выполнения**. Он не ленивый — он вычитывает весь конвейер целиком и складывает результат в `List<T>`. Здесь нет `yield return`, поэтому разделение на два метода не нужно: исключение вылетит сразу.

```csharp
public static List<T> MyToList<T>(this IEnumerable<T> items)
{
    if (items == null) throw new ArgumentNullException(nameof(items));

    return new List<T>(items); // Конструктор List<T> сам обходит весь итератор
}
```

### `MyFirstOrDefault` — ранний выход (short-circuiting)

Как только нашли нужный элемент — немедленно возвращаем его и прекращаем перебор. Если коллекция пуста — возвращаем `default(T)` (null для классов, 0 для int и т.д.).

```csharp
public static T MyFirstOrDefault<T>(this IEnumerable<T> items, Func<T, bool> predicate)
{
    if (items == null)     throw new ArgumentNullException(nameof(items));
    if (predicate == null) throw new ArgumentNullException(nameof(predicate));

    foreach (var item in items)
        if (predicate(item))
            return item; // Ранний выход. Остальные элементы даже не будут запрошены.

    return default(T);
}
```

### `MyAny` — проверка на существование

Та же механика раннего выхода, но возвращает `bool`. Как только нашли хотя бы один — сразу `true`. Если прошли всю коллекцию и ничего нет — `false`.

```csharp
public static bool MyAny<T>(this IEnumerable<T> items, Func<T, bool> predicate)
{
    if (items == null)     throw new ArgumentNullException(nameof(items));
    if (predicate == null) throw new ArgumentNullException(nameof(predicate));

    foreach (var item in items)
        if (predicate(item)) return true; // Нашли — незачем смотреть дальше

    return false;
}
```

### `MyCount` — подсчёт элементов

Без предиката — можно использовать оптимизацию: если источник реализует `ICollection<T>`, его `Count` уже известен за `O(1)`. С предикатом — оптимизация не работает: нам всё равно нужно проверить каждый элемент.

```csharp
// Версия без предиката — с оптимизацией
public static int MyCount<T>(this IEnumerable<T> items)
{
    if (items == null) throw new ArgumentNullException(nameof(items));

    // Оптимизация: если источник уже знает свой размер — не перебираем
    if (items is ICollection<T> collection) return collection.Count; // O(1)

    int count = 0;
    foreach (var _ in items) count++; // O(n)
    return count;
}

// Версия с предикатом — оптимизация неприменима
public static int MyCount<T>(this IEnumerable<T> items, Func<T, bool> predicate)
{
    if (items == null)     throw new ArgumentNullException(nameof(items));
    if (predicate == null) throw new ArgumentNullException(nameof(predicate));

    // ICollection.Count здесь бесполезен — нам нужно проверить КАЖДЫЙ элемент
    int count = 0;
    foreach (var item in items)
        if (predicate(item)) count++;

    return count;
}
```

### `MyOrderBy` — сортировка (жадный итератор)

Сортировка — особый случай. Она обязана знать **все** элементы, прежде чем вернуть хотя бы один: вдруг последний элемент окажется самым «меньшим»?

`MyOrderBy` — **ленивый снаружи, но жадный при первой итерации**: при вызове метода ничего не происходит, но в момент первого `MoveNext()` внутри итератора вся коллекция вычитывается в память и сортируется целиком. Только после этого элементы начинают отдаваться по одному.

> ⚠️ **Критический баг наивной реализации: нестабильная сортировка.**  
> Первое, что приходит в голову, — вычитать всё в `List<T>` и вызвать `list.Sort(...)`. Но `List<T>.Sort()` и `Array.Sort()` используют Introsort — алгоритм **нестабильный**: у элементов с одинаковым ключом относительный порядок не определён. Настоящий LINQ гарантирует **стабильную сортировку**: элементы с равными ключами сохраняют исходный порядок.  
> Пример, где это важно: `.OrderBy(u => u.Department)` — сотрудники должны остаться в том же порядке внутри каждого отдела, в котором они пришли из базы.  
> Коварство в том, что на коротких наборах (до 16 элементов) Introsort откатывается к сортировке вставками, а она стабильна. Баг не виден на тестовых данных и вылезает в продакшене на сотнях записей.

Исправление — сортируем не сами элементы, а **индексы**, а при равных ключах решает исходный индекс. Так делает и настоящий LINQ:

```csharp
public static IEnumerable<T> MyOrderBy<T, TKey>(this IEnumerable<T> items, Func<T, TKey> keySelector)
{
    if (items == null)       throw new ArgumentNullException(nameof(items));
    if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));

    return OrderByIterator(items, keySelector);
}

private static IEnumerable<T> OrderByIterator<T, TKey>(IEnumerable<T> items, Func<T, TKey> keySelector)
{
    // 1. Вычитываем всё в память и считаем ключ ОДИН раз для каждого элемента
    var buffer = new List<T>(items);
    var keys = new TKey[buffer.Count];
    for (int i = 0; i < keys.Length; i++)
        keys[i] = keySelector(buffer[i]);

    // 2. Сортируем не элементы, а массив их индексов: 0, 1, 2, ... n-1
    var map = new int[buffer.Count];
    for (int i = 0; i < map.Length; i++)
        map[i] = i;

    var comparer = Comparer<TKey>.Default;
    Array.Sort(map, (a, b) =>
    {
        int result = comparer.Compare(keys[a], keys[b]);
        return result != 0 ? result : a.CompareTo(b); // ← стабилизатор: равные ключи → исходный порядок
    });

    // 3. Отдаём элементы в порядке, который задали индексы
    foreach (int index in map)
        yield return buffer[index];
}
```

Почему это работает: индексы у всех элементов разные, поэтому сравнение никогда не возвращает «поровну» — сортировка получает строгий порядок, и любой алгоритм, даже нестабильный, выдаст один и тот же стабильный результат.

> 📌 Это учебная версия для одного ключа. В Части 10 мы переделаем `MyOrderBy` так, чтобы он возвращал `IMyOrderedEnumerable<T>` и умел `ThenBy`, а в Части 14 посмотрим, чем настоящий `OrderBy` хитрее.

---

## 📊 Часть 5. Классификация методов LINQ

| Категория | Методы | Поведение | Комментарий |
|---|---|---|---|
| **Ленивые** | `Where`, `Select` | Отложенное выполнение | Строят конвейер, не трогая данные |
| **Материализаторы** | `ToList`, `ToArray` | Немедленное выполнение | Вычитывают всё в RAM |
| **Агрегаты** | `Count`, `Sum`, `Min`, `Max` | Немедленное выполнение | Проходят всю последовательность (у `Count` без предиката бывает быстрый путь за O(1)) |
| **Ранний выход** | `Any`, `First`, `FirstOrDefault` | Немедленное, с остановкой | Останавливаются при нахождении ответа |
| **Жадные внутри** | `OrderBy`, `GroupBy` | Ленивый фасад, жадное нутро | Должны вычитать всё для сортировки/группировки |
| **Составные** | `ThenBy` | Ленивый, добавляет компаратор | Работает только на упорядоченной последовательности (`IOrderedEnumerable<T>`; у нас — `IMyOrderedEnumerable<T>`), оборачивает предыдущий компаратор |

---

## 🔁 Часть 6. Как ленивые методы общаются в конвейере

Это ключевое понимание для оценки производительности. Разберём на примере:

```csharp
var result = items              // 1 000 000 записей
    .MyWhere(p => p.Age > 20)   // Ленивый фильтр
    .MyOrderBy(p => p.Name)     // Жадный итератор
    .MySelect(p => p.Name);     // Ленивая проекция
```

**Как данные проходят через конвейер:**

```
[items: 1 000 000 элементов]
         │
         │  Ленивый стрим: элементы передаются по одному
         ▼
    ┌─────────────────────────────────────────────┐
    │  MyWhere(p => p.Age > 20)                   │
    │  Проверяет каждый элемент, пропускает 10    │
    │  Не создаёт промежуточной коллекции         │
    └───────────────────┬─────────────────────────┘
                        │ 10 элементов (поштучно)
                        ▼
    ┌─────────────────────────────────────────────┐
    │  MyOrderBy(p => p.Name)            [ЖАДНЫЙ] │
    │  ← вычитывает ВСЕ 10 в List<T>              │
    │  → сортирует                                │
    │  → отдаёт поштучно                          │
    └───────────────────┬─────────────────────────┘
                        │ 10 элементов (поштучно)
                        ▼
    ┌─────────────────────────────────────────────┐
    │  MySelect(p => p.Name)                      │
    │  Преобразует каждый элемент, не копит       │
    └───────────────────┬─────────────────────────┘
                        │
                        ▼
              [result: 10 строк имён]
```

| Шаг | Метод | Что происходит |
|---|---|---|
| 1 | `MyWhere` | Проверяет 1 000 000 записей, пропускает дальше только **10** |
| 2 | `MyOrderBy` | Материализует в память **только эти 10** и сортирует их |
| 3 | `MySelect` | Берёт 10 отсортированных и достаёт из них имена |

`MyWhere` не создаёт промежуточный список. Он передаёт элементы по одному, как только они проходят проверку. `MyOrderBy` «увидит» и скопирует в память ровно столько, сколько прошло фильтр.

---

## 🌳 Часть 7. `IQueryable` и деревья выражений

### В чём принципиальная разница

> 💡 **Ключевая мысль:** `IQueryable<T>` — это **не коллекция**. Это DSL для построения запроса. Ты не работаешь с данными — ты составляешь описание того, *как* их получить. Провайдер читает это описание и переводит на язык источника: SQL, OData, Elasticsearch. Данные появляются только в момент материализации.

Когда данные в памяти — мы используем `IEnumerable`. Когда в базе данных — нужен `IQueryable`.

Ключевой вопрос: **что происходит с лямбдой**?

```csharp
// IEnumerable — лямбда компилируется в делегат (исполняемый код)
Func<User, bool> filter = u => u.Age > 18;
// Это "чёрный ящик". Мы не можем его прочитать, только вызвать.

// IQueryable — лямбда превращается в дерево выражений (данные о коде)
Expression<Func<User, bool>> filterExpr = u => u.Age > 18;
// Это "чертёж". Его можно проанализировать, изменить, перевести в SQL.
```

Что получится — делегат или дерево, решает **тип, в который присваивается лямбда**: `Func<...>` или `Expression<Func<...>>`. Поэтому `Enumerable.Where` принимает `Func<T, bool>`, а `Queryable.Where` — `Expression<Func<T, bool>>`.

**Аналогия:** делегат — это рецепт пирога, который ты сразу начинаешь готовить. Expression — это *схема* рецепта. Ты не печёшь по ней сразу: ты можешь её прочитать, передать, перевести на другой язык.

### Анатомия дерева выражений

Возьмём выражение `age >= 18`. Оно состоит из трёх узлов:

```
        BinaryExpression (GreaterThanOrEqual)
           /                    \
ParameterExpression(age)    ConstantExpression(18)
```

В коде это выглядит так:

```csharp
// Собираем дерево вручную
ParameterExpression ageParam = Expression.Parameter(typeof(int), "age");
ConstantExpression constant18 = Expression.Constant(18);
BinaryExpression comparison = Expression.GreaterThanOrEqual(ageParam, constant18);

Expression<Func<int, bool>> lambdaExpr =
    Expression.Lambda<Func<int, bool>>(comparison, ageParam);

// Превращаем чертёж в исполняемый делегат
Func<int, bool> isAdult = lambdaExpr.Compile();
Console.WriteLine(isAdult(25)); // true
```

В момент вызова `.Compile()` Runtime анализирует дерево узлов и **генерирует MSIL-код в оперативной памяти**. JIT компилирует его в машинные инструкции. Из данных — в живой, работающий код.

### Как провайдер переводит дерево в SQL

| `ExpressionType` в C# | SQL-оператор |
|---|---|
| `Equal` | `=` |
| `NotEqual` | `<>` |
| `GreaterThan` | `>` |
| `LessThan` | `<` |
| `AndAlso` | `AND` |
| `OrElse` | `OR` |

Entity Framework проходит по дереву, читает тип каждого узла и строит SQL-строку:

```
Equal(MemberAccess(u, "Name"), Constant("Admin"))
→  WHERE Name = 'Admin'
```

### IEnumerable vs IQueryable — наглядное сравнение

```csharp
// ❌ ПЛОХО: db.Users — IEnumerable
// Шаг 1: База отдаёт ВСЕ 1 000 000 записей в память
// Шаг 2: Where работает уже в памяти сервера приложений
var slow = ((IEnumerable<User>)db.Users)
    .Where(u => u.Age > 18)
    .ToList();

// ✅ ХОРОШО: db.Users — IQueryable (DbSet<T> уже является IQueryable)
// Шаг 1: Строится дерево выражений
// Шаг 2: Провайдер генерирует: SELECT * FROM Users WHERE Age > 18
// Шаг 3: База возвращает только нужные строки
var fast = db.Users
    .Where(u => u.Age > 18)
    .ToList();
```

### Когда нужен `.AsQueryable()`?

Если `db.Users` — это `DbSet<T>` в Entity Framework, вызов `.AsQueryable()` **избыточен**: `DbSet<T>` уже реализует `IQueryable<T>`.

Метод становится полезным в двух сценариях:

**1. Унификация источников данных:**
```csharp
// Компромиссный приём — один метод работает и с List<T>, и с DbSet<T>
public IEnumerable<User> Filter(IEnumerable<User> users)
{
    return users.AsQueryable().Where(u => u.IsActive);
    // Если пришёл DbSet (он уже IQueryable) → AsQueryable вернёт его как есть, и запрос уйдёт в БД
    // Если пришёл List или любой другой IEnumerable → он оборачивается в EnumerableQuery<T>,
    //   и фильтрация выполняется в памяти
}
```
> ⚠️ В архитектуре лучше не смешивать `IQueryable` и `IEnumerable` в одном контракте. Это размывает ответственность: метод не объявляет, ожидает ли он коллекцию в памяти или провайдер запросов.

**2. Тесты: подсунуть список там, где ожидается `IQueryable<T>`:**
```csharp
// Код под тестом принимает IQueryable<User> (например, чтобы дополнять запрос)
IQueryable<User> fakeUsers = new List<User>
{
    new User { Name = "Anna",  IsActive = true  },
    new User { Name = "Boris", IsActive = false },
}.AsQueryable();
```
> ⚠️ `EnumerableQuery` выполняет запрос в памяти обычным LINQ to Objects. Он ничего не знает ни про SQL, ни про ограничения трансляции EF Core (регистр строк, обработку `null`, неподдерживаемые методы...) — поэтому такой тест не докажет, что запрос вообще переводится в SQL. Для этого нужен настоящий провайдер: SQLite или контейнер с реальной БД.

> 🧠 **Правило наставника:** С EF Core `.AsQueryable()` почти всегда не нужен. Пиши просто `db.Users.Where(...)`.

### Исключения трансляции: когда провайдер не может перевести лямбду в SQL

Глава про `IQueryable` была бы неполной без самой болезненной части реального опыта. Не любую лямбду можно превратить в дерево выражений и перевести в SQL.

```csharp
// ✅ EF Core знает, как перевести это в SQL
db.Users.Where(u => u.Age > 18 && u.IsActive)
// → SELECT * FROM Users WHERE Age > 18 AND IsActive = 1

// ❌ EF Core не знает про MyCustomHelper — упадёт в рантайме!
db.Users.Where(u => MyCustomHelper.IsEligible(u.Age))
// → InvalidOperationException: The LINQ expression '...' could not be translated.
//   Either rewrite the query in a form that can be translated, or switch to client
//   evaluation explicitly by inserting a call to 'AsEnumerable', 'AsAsyncEnumerable',
//   'ToList', or 'ToListAsync'.
```

Провайдер не имеет понятия, что такое `MyCustomHelper.IsEligible`. Он видит в дереве выражений узел `MethodCallExpression` с вызовом произвольного .NET-метода и не может сопоставить его ни с каким SQL-конструктом. Исключение вылетает не при компиляции и не при построении запроса, а в момент выполнения — на `ToList()`, `FirstOrDefault()` и т.п.

**Главное правило, которое надо держать в голове:** EF Core смотрит, от чего зависит кусок выражения.
- Зависит **только от внешних значений** (захваченные переменные, поля, свойства, вызовы методов без участия `u`) — EF вычислит его у себя, в приложении, и передаст в SQL **параметром**.
- Зависит **от строки таблицы** (`u.Age`, `u.Name`...) — EF обязан превратить это в SQL. Если метод ему незнаком, получишь исключение выше.

**Типичные ловушки и безопасные варианты:**

```csharp
// ❌ Любой свой метод, в который передаётся значение из строки
.Where(u => IsAdult(u.Age))

// ❌ Свой метод расширения (EF Core о нём ничего не знает)
.Where(u => u.Name.IsNullOrEmpty())

// ✅ Встроенный string.IsNullOrEmpty EF Core переводит сам
.Where(u => string.IsNullOrEmpty(u.Name))

// ✅ Захваченная переменная: значение уходит в SQL ПАРАМЕТРОМ (WHERE Age > @__minAge_0),
//    а не вшивается в текст запроса — на этом держится кэш запросов
var minAge = 18;
.Where(u => u.Age > minAge)

// ✅ Свойство захваченного объекта — тоже можно: оно вычисляется до отправки запроса
var policy = new AgePolicy();
.Where(u => u.Age > policy.MinAge)
```

Литералы (`u.Age > 18`) попадают в SQL константами, захваченные переменные — параметрами.

**Если без своего метода никак**, сначала сузь выборку в БД, а потом осознанно переходи на обработку в памяти:

```csharp
var users = db.Users
    .Where(u => u.IsActive)                          // уйдёт в SQL
    .AsEnumerable()                                  // дальше — LINQ to Objects, в памяти приложения
    .Where(u => MyCustomHelper.IsEligible(u.Age))
    .ToList();
```
> ⚠️ Следи за тем, что остаётся до `AsEnumerable()`. Если первый фильтр слабый, в память приложения приедет вся таблица.

> 💡 **Единственное место, где неподдерживаемый метод «прощается»:** финальный `Select`. Если свой метод стоит только в самой последней проекции, EF Core вытащит нужные колонки, а метод применит уже на клиенте. В `Where`, `OrderBy`, `GroupBy`, `Join` так не сработает.

**Правило:** если код компилируется, это не значит, что он будет работать с `IQueryable`. Тестируй реальные запросы к БД и читай SQL: `optionsBuilder.LogTo(Console.WriteLine)` в настройках контекста пишет все запросы в лог, а `query.ToQueryString()` показывает SQL конкретного запроса, не выполняя его (оба метода появились в EF Core 5).

---

## ⚠️ Часть 8. Золотые правила и типичные ошибки

### Правило 1: Не вызывай `ToList()` слишком рано

```csharp
// ❌ ПЛОХО
var users = db.Users.ToList()           // Вся таблица в памяти!
                    .Where(u => u.Age > 18);

// ✅ ХОРОШО
var users = db.Users.Where(u => u.Age > 18)
                    .ToList();          // В память попадают только отфильтрованные
```

### Правило 2: Передавай `IQueryable` между методами

```csharp
// ❌ ПЛОХО — обрываем цепочку IQueryable
public IEnumerable<User> GetAdults()  // IEnumerable — всё, что вызывающий допишет дальше (Where, OrderBy...), выполнится уже в памяти
{
    return db.Users.Where(u => u.Age > 18);
}

// ✅ ХОРОШО — сохраняем возможность дополнять запрос
public IQueryable<User> GetAdults()   // IQueryable — дописанные снаружи Where, OrderBy, Select тоже уйдут в SQL
{
    return db.Users.Where(u => u.Age > 18);
}
```

### Правило 3: Проверяй интерфейсы для оптимизации

```csharp
// Не перебирай то, что уже посчитано
if (items is ICollection<T> collection)
    return collection.Count; // O(1)

// Иначе придётся пройти всё
int count = 0;
foreach (var _ in items) count++; // O(n)
```

---

## 🗑️ Часть 9. LINQ и Garbage Collector: цена удобства

Это тема, о которой молчат большинство туториалов. LINQ элегантен — но не бесплатен. Каждый вызов в цепочке создаёт нагрузку на GC.

### Что аллоцируется при работе LINQ

```csharp
var result = items
    .Where(x => x.Age > 18)    // объект-итератор для Where
    .Select(x => x.Name)       // объект-итератор для Select
    .ToList();                 // List<string> и его внутренний массив
```

Грубо: по объекту на каждый ленивый оператор плюс результирующая коллекция. Настоящий .NET местами склеивает операторы (например, `Where` + `Select` над массивом или `List<T>` превращаются в один итератор), поэтому точное число зависит от версии и типа источника — но порядок такой: **несколько объектов в куче на одну цепочку**.

На редком вызове — незаметно. На горячем пути с тысячами вызовов в секунду — давление на GC становится измеримым.

**Лямбды с захватом переменных (closures) — отдельная история:**

```csharp
int minAge = 18; // внешняя переменная

// ❌ Захват переменной — компилятор создаёт скрытый класс-замыкание в куче
items.Where(x => x.Age > minAge)

// ✅ Константа — замыкания нет, лямбда может быть кэширована как статический делегат
items.Where(x => x.Age > 18)
```

Когда лямбда захватывает внешнюю переменную, компилятор генерирует скрытый класс `<>c__DisplayClass` и создаёт его экземпляр при входе в блок, где объявлена переменная (для переменной уровня метода — **один раз на вызов метода**, а не на каждый элемент). Плюс каждый раз создаётся новый объект-делегат, привязанный к этому замыканию. Лямбда без захвата, наоборот, превращается в делегат, который компилятор создаёт один раз и кэширует в статическом поле. Так что при вызове метода в цикле (тысячи раз в секунду) замыкание — это тысячи лишних объектов. Именно поэтому closure на горячем пути — проблема.

> 💡 **Приём:** пиши `static` перед лямбдой (`items.Where(static x => x.Age > 18)`, C# 9+). Компилятор запретит случайный захват переменной — и ты сразу увидишь место, где кэшируемый делегат превратился бы в замыкание.

### Когда LINQ вырезают из горячего пути

```csharp
// LINQ — удобно, но не бесплатно
public int CountActive(IEnumerable<User> users)
    => users.Count(u => u.IsActive);
    // Перебор через интерфейс (перечислитель в куче; в .NET 10 для массива и List<T> этого уже нет)
    // + вызов делегата на каждый элемент.
    // Замыкания здесь нет: лямбда ничего не захватывает, её делегат закэширован.

// Чистый for — ноль аллокаций
public int CountActive(User[] users)
{
    int count = 0;
    for (int i = 0; i < users.Length; i++)
        if (users[i].IsActive) count++;
    return count;
}

// Современный C# — ReadOnlySpan<T>: ноль аллокаций, никаких делегатов и интерфейсных вызовов
public int CountActive(ReadOnlySpan<User> users)
{
    int count = 0;
    foreach (ref readonly var user in users)
        if (user.IsActive) count++;
    return count;
}
```

Массив приводится к `ReadOnlySpan<T>` неявно, а из `List<T>` span достаётся через `CollectionsMarshal.AsSpan(list)` — пока span используется, список нельзя изменять (добавление и удаление могут перевыделить внутренний массив, и span будет указывать на устаревший буфер).

### Правило выбора

| Контекст | Подход |
|---|---|
| Бизнес-логика, обработка запросов | LINQ — читаемость важнее микро-оптимизаций |
| Горячий путь (по данным профайлера) | `for` / `Span<T>` / специализированные структуры |
| Геймдев, real-time системы | LINQ на hot path обычно под запретом |
| Высоконагруженный сервис | Профилируй сначала, оптимизируй потом — не наоборот |

> 🧠 **Вывод наставника:** Знание аллокаций не значит «никогда не использовать LINQ». Это значит — понимать цену и осознанно принимать решение. Сначала профилировщик (`dotnet-trace`, BenchmarkDotNet), потом оптимизация.

---

## 🔗 Часть 10. `ThenBy` — многоуровневая сортировка

### Почему `ThenBy` не может быть просто методом на `IEnumerable<T>`

Наивная идея выглядит так:

```csharp
// ❌ Так не работает
public static IEnumerable<T> MyThenBy<T, TKey>(
    this IEnumerable<T> items, // обычный IEnumerable
    Func<T, TKey> keySelector)
{
    // Как узнать, по какому ключу уже отсортировано? — НИКАК
}
```

`IEnumerable<T>` — это просто последовательность. Она **не помнит** как была отсортирована. Информация о предыдущем компараторе безвозвратно потеряна.

И ещё одна ловушка. Казалось бы — просто отсортируй ещё раз:

```csharp
// ❌ Плохая идея — пересортировать уже отсортированное
.MyOrderBy(p => p.Name)   // отсортировали по Name
.MyThenBy(p => p.Age)     // взяли результат и отсортировали только по Age
                          // → главным ключом стал Age, а Name — в лучшем случае второстепенным
```

Если сортировка нестабильная, порядок по `Name` внутри одинаковых `Age` вообще не гарантирован. Если стабильная — приоритеты ключей окажутся перевёрнутыми (сначала `Age`, потом `Name`), а нужно наоборот. `ThenBy` должен сортировать **по двум ключам одновременно**, за один проход сортировки. Значит, оба компаратора должны быть известны к моменту сортировки.

### Решение: специальный интерфейс + цепочка компараторов

Нам нужен интерфейс-маркер, который «запоминает» предыдущий компаратор и позволяет добавить к нему следующий:

```csharp
// Шаг 1 — интерфейс-контракт для упорядоченных последовательностей
public interface IMyOrderedEnumerable<T> : IEnumerable<T>
{
    // ThenBy вызывает именно этот метод, чтобы добавить вторичный ключ
    IMyOrderedEnumerable<T> CreateOrderedEnumerable<TKey>(Func<T, TKey> keySelector);
}
```

```csharp
// Шаг 2 — реализация: класс хранит источник и текущий компаратор
public class MyOrderedEnumerable<T> : IMyOrderedEnumerable<T>
{
    private readonly IEnumerable<T> _source;
    private readonly Comparison<T> _comparison; // накопленный компаратор

    public MyOrderedEnumerable(IEnumerable<T> source, Comparison<T> comparison)
    {
        _source = source;
        _comparison = comparison;
    }

    // ThenBy вызывает это — оборачиваем старый компаратор новым, не заменяем!
    public IMyOrderedEnumerable<T> CreateOrderedEnumerable<TKey>(Func<T, TKey> keySelector)
    {
        Comparison<T> newComparison = (a, b) =>
        {
            int result = _comparison(a, b);           // 1. сначала старый ключ
            if (result != 0) return result;           // 2. элементы различаются — готово
            return Comparer<TKey>.Default.Compare(    // 3. только если равны — новый ключ
                keySelector(a), keySelector(b));
        };

        return new MyOrderedEnumerable<T>(_source, newComparison);
    }

    // Сортировка происходит здесь — один раз при первом MoveNext()
    public IEnumerator<T> GetEnumerator()
    {
        var buffer = new List<T>(_source);   // 1. вычитываем всё в память

        var map = new int[buffer.Count];     // 2. сортируем индексы, а не сами элементы
        for (int i = 0; i < map.Length; i++)
            map[i] = i;

        Array.Sort(map, (a, b) =>
        {
            int result = _comparison(buffer[a], buffer[b]);
            return result != 0 ? result : a.CompareTo(b); // ← стабилизатор: равные → исходный порядок
        });

        foreach (int index in map)           // 3. отдаём в отсортированном порядке
            yield return buffer[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
```

```csharp
// Шаг 3 — публичные методы
public static IMyOrderedEnumerable<T> MyOrderBy<T, TKey>(
    this IEnumerable<T> items,
    Func<T, TKey> keySelector)
{
    if (items == null)       throw new ArgumentNullException(nameof(items));
    if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));

    return new MyOrderedEnumerable<T>(items, (a, b) =>
        Comparer<TKey>.Default.Compare(keySelector(a), keySelector(b)));
}

public static IMyOrderedEnumerable<T> MyThenBy<T, TKey>(
    this IMyOrderedEnumerable<T> items,  // принимает именно IMyOrderedEnumerable!
    Func<T, TKey> keySelector)
{
    if (items == null)       throw new ArgumentNullException(nameof(items));
    if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));

    return items.CreateOrderedEnumerable(keySelector);
}
```

### Как работает цепочка

```
MyOrderBy(Name)              MyThenBy(Age)
──────────────────           ─────────────────────────────────
comparison = (a,b) =>        newComparison = (a,b) =>
  compare Name                 result = compare Name      ← старый
                               if result != 0 return result
                               return compare Age          ← новый
```

Каждый `ThenBy` **оборачивает** предыдущий компаратор, а не заменяет его. Сортировка происходит за один проход (`Array.Sort` по индексам), и компаратор уже знает все уровни.

```csharp
var people = new[]
{
    new { Name = "Anna",  Age = 25 },
    new { Name = "Boris", Age = 25 },
    new { Name = "Anna",  Age = 20 },
};

var sorted = people
    .MyOrderBy(p => p.Name)
    .MyThenBy(p => p.Age)
    .MyToList();

// Anna  20
// Anna  25
// Boris 25
```

### Итог: почему именно такая структура

| Вопрос | Ответ |
|---|---|
| Почему `IMyOrderedEnumerable`? | Чтобы сохранить компаратор между вызовами `OrderBy` и `ThenBy` |
| Почему обёртка, а не пересортировка? | Пересортировка меняет приоритет ключей местами (а на нестабильной сортировке ещё и теряет прежний порядок) |
| Почему `if (result != 0) return result`? | Это и есть определение вторичной сортировки |
| Почему сортировка только в `GetEnumerator()`? | Отложенное выполнение — сортируем один раз при итерации |
| Зачем индексы в `GetEnumerator()`? | Стабильность: если все ключи равны, порядок решает исходная позиция элемента |

> 🧠 **Одной фразой:** `IMyOrderedEnumerable` существует только для того, чтобы **передать компаратор** от `MyOrderBy` к `MyThenBy`, а обёртка компараторов — чтобы **не потерять предыдущую сортировку**.

> ⚠️ **Упрощение.** В нашей версии `keySelector` вызывается заново при каждом сравнении — то есть порядка n·log n раз, а не n. Настоящий LINQ считает ключ один раз на элемент и кэширует его (см. Часть 14). Если селектор дорогой или с побочными эффектами, разница заметна.

---

## 📦 Часть 11. `GroupBy` — разбиваем на группы

### Что должен вернуть `GroupBy`

В отличие от `Where` и `Select`, которые возвращают `IEnumerable<T>`, `GroupBy` возвращает **последовательность групп**. Каждая группа — это `IGrouping<TKey, T>`: она одновременно является и коллекцией элементов, и имеет свойство `Key`.

```csharp
// Стандартный контракт (упрощённо: в BCL оба параметра типа помечены `out`, как у IEnumerable<out T>)
public interface IGrouping<TKey, T> : IEnumerable<T>
{
    TKey Key { get; }  // ключ группы — то, по чему группировали
}
```

### Реализация

```csharp
// Шаг 1 — реализация группы: ключ + список элементов
public class MyGrouping<TKey, T> : IGrouping<TKey, T>
{
    private readonly List<T> _items;

    public TKey Key { get; }

    public MyGrouping(TKey key)
    {
        Key = key;
        _items = new List<T>();
    }

    public void Add(T item) => _items.Add(item);

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
```

```csharp
// Шаг 2 — публичный метод + приватный итератор (паттерн Fail Fast)
public static IEnumerable<IGrouping<TKey, T>> MyGroupBy<T, TKey>(
    this IEnumerable<T> items,
    Func<T, TKey> keySelector)
{
    if (items == null)       throw new ArgumentNullException(nameof(items));
    if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));

    return MyGroupByIterator(items, keySelector);
}

private static IEnumerable<IGrouping<TKey, T>> MyGroupByIterator<T, TKey>(
    IEnumerable<T> items,
    Func<T, TKey> keySelector)
{
    // groups — группы в порядке первого появления ключа: именно в таком порядке мы их и отдадим
    // lookup — быстрый поиск группы по ключу. Порядок обхода Dictionary официально НЕ определён,
    //          поэтому на него мы не полагаемся
    var groups = new List<MyGrouping<TKey, T>>();
    var lookup = new Dictionary<TKey, MyGrouping<TKey, T>>();

    foreach (T item in items)
    {
        TKey key = keySelector(item);   // ⚠️ вернулся null — TryGetValue ниже бросит ArgumentNullException: Dictionary не принимает null-ключи (см. Часть 14)

        if (!lookup.TryGetValue(key, out var group))
        {
            group = new MyGrouping<TKey, T>(key);
            lookup[key] = group;
            groups.Add(group);
        }

        group.Add(item);
    }

    foreach (var group in groups)
        yield return group;
}
```

### Как работает внутри

```
Итерация источника:
  Anna  → key="IT" → создать группу IT,  добавить Anna
  Boris → key="HR" → создать группу HR,  добавить Boris
  Clara → key="IT" → группа IT уже есть, добавить Clara
  Dima  → key="HR" → группа HR уже есть, добавить Dima

lookup (поиск по ключу):        groups (порядок появления ключей):
  "IT" → группа IT               [0] IT → [Anna, Clara]
  "HR" → группа HR               [1] HR → [Boris, Dima]

yield return каждую группу из groups — в порядке появления ключей
```

`Dictionary` ищет группу по ключу за O(1), а `List` хранит порядок. Как и `List` в `OrderBy`, эти структуры **материализуют данные** внутри итератора, сохраняя отложенное выполнение снаружи.

> ⚠️ **Ограничение:** `Dictionary` не принимает `null` в качестве ключа. Если `keySelector` вернёт `null`, наш `MyGroupBy` бросит `ArgumentNullException`. Настоящий `GroupBy` такое переваривает — подробности в Части 14.

```csharp
var people = new[]
{
    new { Name = "Anna",  Dept = "IT" },
    new { Name = "Boris", Dept = "HR" },
    new { Name = "Clara", Dept = "IT" },
    new { Name = "Dima",  Dept = "HR" },
};

foreach (var group in people.MyGroupBy(p => p.Dept))
{
    Console.WriteLine($"[{group.Key}]");
    foreach (var p in group)
        Console.WriteLine($"  {p.Name}");
}

// [IT]
//   Anna
//   Clara
// [HR]
//   Boris
//   Dima
```

### Сравнение с `MyOrderBy`

| | `MyOrderBy` | `MyGroupBy` |
|---|---|---|
| Нужно знать все элементы? | ✅ Да | ✅ Да |
| Промежуточная структура | `List<T>` + массив индексов | `Dictionary<TKey, Group>` + `List` групп |
| Отложенное выполнение | ✅ через итератор | ✅ через итератор |
| Возвращает | `IEnumerable<T>` | `IEnumerable<IGrouping<TKey, T>>` |

---

## 🧪 Часть 12. Боевые тесты

Теория — это хорошо. Но настоящее понимание проверяется на сложных цепочках. Вот шесть задач — от комбинированных цепочек до граничных случаев и проверки стабильности сортировки. Полный код, на котором всё запускается, — в Части 13.

### Данные

```csharp
var employees = new[]
{
    new { Name = "Anna",  Dept = "IT", Salary = 90000,  Age = 30 },
    new { Name = "Boris", Dept = "HR", Salary = 60000,  Age = 45 },
    new { Name = "Clara", Dept = "IT", Salary = 90000,  Age = 25 },
    new { Name = "Dima",  Dept = "HR", Salary = 75000,  Age = 35 },
    new { Name = "Elena", Dept = "IT", Salary = 110000, Age = 28 },
    new { Name = "Fedor", Dept = "HR", Salary = 60000,  Age = 45 },
    new { Name = "Galya", Dept = "IT", Salary = 90000,  Age = 30 },
};
```

---

### Тест 1 — `GroupBy` + `MyCount` внутри группы

```csharp
// Сколько сотрудников в каждом отделе зарабатывают больше 80к?
var result = employees
    .MyGroupBy(e => e.Dept)
    .MyOrderBy(g => g.Key);

foreach (var group in result)
{
    int highEarners = group.MyCount(e => e.Salary > 80000);
    Console.WriteLine($"{group.Key}: {highEarners} высокооплачиваемых");
}

// HR: 0 высокооплачиваемых
// IT: 4 высокооплачиваемых
```

> Что проверяем: `MyCount` с предикатом корректно работает внутри `IGrouping`. В HR максимальная зарплата — 75к, поэтому там честный ноль.

---

### Тест 2 — `ThenBy` на трёх уровнях

```csharp
// Сортировка: отдел → зарплата → возраст
var result = employees
    .MyOrderBy(e => e.Dept)
    .MyThenBy(e => e.Salary)
    .MyThenBy(e => e.Age);

foreach (var e in result)
    Console.WriteLine($"{e.Dept,-5} {e.Salary,8} {e.Age,4}  {e.Name}");

// HR       60000   45  Boris
// HR       60000   45  Fedor   ← равны по всем трём ключам: стабильная сортировка сохраняет исходный порядок
// HR       75000   35  Dima
// IT       90000   25  Clara
// IT       90000   30  Anna
// IT       90000   30  Galya
// IT      110000   28  Elena
```

> Что проверяем: цепочка из трёх компараторов не теряет ни один уровень сортировки.

---

### Тест 3 — `GroupBy` → `OrderBy` групп → `OrderBy` внутри группы

```csharp
// Группы отсортированы по размеру, внутри каждой — по имени
var result = employees
    .MyGroupBy(e => e.Dept)
    .MyOrderBy(g => g.MyCount(_ => true))  // по количеству людей в группе
    .MyThenBy(g => g.Key);                 // затем по названию отдела

foreach (var group in result)
{
    Console.WriteLine($"[{group.Key}] ({group.MyCount(_ => true)} чел.)");

    foreach (var e in group.MyOrderBy(e => e.Name))
        Console.WriteLine($"  {e.Name}");
}

// [HR] (3 чел.)
//   Boris
//   Dima
//   Fedor
// [IT] (4 чел.)
//   Anna
//   Clara
//   Elena
//   Galya
```

> Что проверяем: вложенные вызовы наших методов друг на друге. Группы сами являются `IEnumerable<T>`, значит на них работают все наши расширения.

---

### Тест 4 — `GroupBy` внутри `GroupBy`

```csharp
// Группировка по отделу, затем по зарплате внутри каждой группы
var result = employees
    .MyGroupBy(e => e.Dept)
    .MyOrderBy(g => g.Key);

foreach (var deptGroup in result)
{
    Console.WriteLine($"[{deptGroup.Key}]");

    var bySalary = deptGroup
        .MyGroupBy(e => e.Salary)
        .MyOrderBy(g => g.Key);

    foreach (var salGroup in bySalary)
    {
        var names = salGroup.MyOrderBy(e => e.Name);
        Console.WriteLine($"  {salGroup.Key,8}: {string.Join(", ", names.MySelect(e => e.Name))}");
    }
}

// [HR]
//      60000: Boris, Fedor
//      75000: Dima
// [IT]
//      90000: Anna, Clara, Galya
//     110000: Elena
```

> Что проверяем: двухуровневая группировка. `MyGroupBy` внутри `MyGroupBy`.

---

### Тест 5 — Граничные случаи (edge cases)

```csharp
// 1. Пустая коллекция — не должна падать, возвращает нули
var empty = new List<int>();
Console.WriteLine(empty.MyCount(x => x > 0));                    // 0
Console.WriteLine(empty.MyOrderBy(x => x).MyCount(_ => true));   // 0
Console.WriteLine(empty.MyGroupBy(x => x).MyCount(_ => true));   // 0

// 2. Один элемент
var single = new[] { 42 };
Console.WriteLine(single.MyOrderBy(x => x).MyFirstOrDefault(_ => true));      // 42
Console.WriteLine(single.MyGroupBy(x => x).MyFirstOrDefault(_ => true).Key);  // 42

// 3. Все элементы одинаковые
var same = new[] { 1, 1, 1, 1 };
Console.WriteLine(same.MyGroupBy(x => x).MyCount(_ => true));    // 1 группа
Console.WriteLine(same.MyCount(x => x == 1));                    // 4

// 4. null-элементы внутри коллекции
var withNulls = new[] { "a", null, "b", null, "c" };
Console.WriteLine(withNulls.MyCount(x => x == null));            // 2

// Наш MyGroupBy не умеет null-ключи (Dictionary их не принимает),
// поэтому подменяем null заглушкой прямо в селекторе ключа
foreach (var g in withNulls.MyGroupBy(x => x ?? "<null>"))
    Console.WriteLine($"{g.Key}: {g.MyCount(_ => true)}");
// a: 1
// <null>: 2
// b: 1
// c: 1
```

---

### Тест 6 — Стабильность сортировки на большом наборе

```csharp
// 100 элементов, ключ принимает всего 3 значения. Внутри одного ключа
// элементы обязаны остаться в исходном порядке — то есть по возрастанию Id
var many = new List<(int Id, int Group)>();
for (int i = 0; i < 100; i++)
    many.Add((i, i % 3));

var sorted = many.MyOrderBy(x => x.Group).MyToList();

bool stable = true;
for (int i = 1; i < sorted.Count; i++)
    if (sorted[i].Group == sorted[i - 1].Group && sorted[i].Id < sorted[i - 1].Id)
        stable = false;

Console.WriteLine(stable ? "Стабильно ✅" : "Порядок равных элементов нарушен ❌");

// Стабильно ✅
```

> Что проверяем: стабильность. На наборе до 16 элементов такой тест прошёл бы даже с наивным `List<T>.Sort()` — поэтому берём 100. Если подменить индексную сортировку обычным `list.Sort(...)`, тест покраснеет.

### Что проверяет каждый тест

| Тест | Что проверяем |
|---|---|
| 1 | `MyCount` с предикатом внутри группы |
| 2 | Три уровня `ThenBy` не теряют порядок |
| 3 | Вложенные вызовы наших методов друг на друге |
| 4 | `GroupBy` внутри `GroupBy` |
| 5 | Граничные случаи — пусто, один элемент, `null` |
| 6 | Стабильность сортировки на наборе из 100 элементов |

---

## 🏁 Часть 13. Итоговый код: весь `MyLinq`

Всё, что мы написали, собрано в одном месте. Скопируй в проект — и тесты из Части 12 заработают как есть. `MyOrderBy` здесь — версия из Части 10 (с поддержкой `ThenBy`): она заменяет упрощённую версию из Части 4.

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq; // нужен только ради интерфейса IGrouping<TKey, T>; методы расширения у нас свои (My*)

// ─── Вспомогательные типы ───────────────────────────────────────────────────

public interface IMyOrderedEnumerable<T> : IEnumerable<T>
{
    IMyOrderedEnumerable<T> CreateOrderedEnumerable<TKey>(Func<T, TKey> keySelector);
}

public class MyOrderedEnumerable<T> : IMyOrderedEnumerable<T>
{
    private readonly IEnumerable<T> _source;
    private readonly Comparison<T> _comparison;

    public MyOrderedEnumerable(IEnumerable<T> source, Comparison<T> comparison)
    {
        _source = source;
        _comparison = comparison;
    }

    public IMyOrderedEnumerable<T> CreateOrderedEnumerable<TKey>(Func<T, TKey> keySelector)
    {
        Comparison<T> newComparison = (a, b) =>
        {
            int result = _comparison(a, b);
            if (result != 0) return result;
            return Comparer<TKey>.Default.Compare(keySelector(a), keySelector(b));
        };
        return new MyOrderedEnumerable<T>(_source, newComparison);
    }

    // Стабильная сортировка: сортируем индексы, а равные элементы различаем по исходной позиции
    public IEnumerator<T> GetEnumerator()
    {
        var buffer = new List<T>(_source);

        var map = new int[buffer.Count];
        for (int i = 0; i < map.Length; i++)
            map[i] = i;

        Array.Sort(map, (a, b) =>
        {
            int result = _comparison(buffer[a], buffer[b]);
            return result != 0 ? result : a.CompareTo(b);
        });

        foreach (int index in map)
            yield return buffer[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class MyGrouping<TKey, T> : IGrouping<TKey, T>
{
    private readonly List<T> _items = new List<T>();
    public TKey Key { get; }
    public MyGrouping(TKey key) { Key = key; }
    public void Add(T item) => _items.Add(item);
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

// ─── Основной класс ─────────────────────────────────────────────────────────

public static class MyLinq
{
    // WHERE — ленивая фильтрация
    public static IEnumerable<T> MyWhere<T>(this IEnumerable<T> items, Func<T, bool> predicate)
    {
        if (items == null)     throw new ArgumentNullException(nameof(items));
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));
        return WhereIterator(items, predicate);
    }
    private static IEnumerable<T> WhereIterator<T>(IEnumerable<T> items, Func<T, bool> predicate)
    {
        foreach (var item in items)
            if (predicate(item)) yield return item;
    }

    // SELECT — ленивая проекция
    public static IEnumerable<R> MySelect<T, R>(this IEnumerable<T> items, Func<T, R> map)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        if (map == null)   throw new ArgumentNullException(nameof(map));
        return SelectIterator(items, map);
    }
    private static IEnumerable<R> SelectIterator<T, R>(IEnumerable<T> items, Func<T, R> map)
    {
        foreach (var item in items) yield return map(item);
    }

    // TOLIST — материализация
    public static List<T> MyToList<T>(this IEnumerable<T> items)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        return new List<T>(items);
    }

    // FIRSTORDEFAULT — немедленный, ранний выход
    public static T MyFirstOrDefault<T>(this IEnumerable<T> items, Func<T, bool> predicate)
    {
        if (items == null)     throw new ArgumentNullException(nameof(items));
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));
        foreach (var item in items)
            if (predicate(item)) return item;
        return default(T);
    }

    // ANY — немедленный, ранний выход, short-circuiting
    public static bool MyAny<T>(this IEnumerable<T> items, Func<T, bool> predicate)
    {
        if (items == null)     throw new ArgumentNullException(nameof(items));
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));
        foreach (var item in items)
            if (predicate(item)) return true;
        return false;
    }

    // COUNT — без предиката, с оптимизацией ICollection
    public static int MyCount<T>(this IEnumerable<T> items)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        if (items is ICollection<T> col) return col.Count;
        int count = 0;
        foreach (var _ in items) count++;
        return count;
    }

    // COUNT — с предикатом, без оптимизации
    public static int MyCount<T>(this IEnumerable<T> items, Func<T, bool> predicate)
    {
        if (items == null)     throw new ArgumentNullException(nameof(items));
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));
        int count = 0;
        foreach (var item in items)
            if (predicate(item)) count++;
        return count;
    }

    // ORDERBY — возвращает IMyOrderedEnumerable для поддержки ThenBy
    public static IMyOrderedEnumerable<T> MyOrderBy<T, TKey>(
        this IEnumerable<T> items,
        Func<T, TKey> keySelector)
    {
        if (items == null)       throw new ArgumentNullException(nameof(items));
        if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));
        return new MyOrderedEnumerable<T>(items, (a, b) =>
            Comparer<TKey>.Default.Compare(keySelector(a), keySelector(b)));
    }

    // THENBY — добавляет вторичный компаратор поверх существующего
    public static IMyOrderedEnumerable<T> MyThenBy<T, TKey>(
        this IMyOrderedEnumerable<T> items,
        Func<T, TKey> keySelector)
    {
        if (items == null)       throw new ArgumentNullException(nameof(items));
        if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));
        return items.CreateOrderedEnumerable(keySelector);
    }

    // GROUPBY — жадный внутри, ленивый снаружи
    public static IEnumerable<IGrouping<TKey, T>> MyGroupBy<T, TKey>(
        this IEnumerable<T> items,
        Func<T, TKey> keySelector)
    {
        if (items == null)       throw new ArgumentNullException(nameof(items));
        if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));
        return MyGroupByIterator(items, keySelector);
    }
    private static IEnumerable<IGrouping<TKey, T>> MyGroupByIterator<T, TKey>(
        IEnumerable<T> items,
        Func<T, TKey> keySelector)
    {
        var groups = new List<MyGrouping<TKey, T>>();               // порядок появления ключей
        var lookup = new Dictionary<TKey, MyGrouping<TKey, T>>();   // поиск группы по ключу
        foreach (T item in items)
        {
            TKey key = keySelector(item);   // null-ключ → ArgumentNullException (см. Часть 14)
            if (!lookup.TryGetValue(key, out var group))
            {
                group = new MyGrouping<TKey, T>(key);
                lookup[key] = group;
                groups.Add(group);
            }
            group.Add(item);
        }
        foreach (var group in groups)
            yield return group;
    }
}
```

---

## 🔩 Часть 14. Под капотом настоящего LINQ — где наша реализация упрощает

Всё, что написано выше — корректно и достаточно для понимания механики. Но если ты хочешь знать, где конкретно стандартный .NET делает иначе и почему — читай дальше. Ниже — сверка с исходниками `System.Linq` из репозитория `dotnet/runtime`.

---

### 1. `GroupBy` в реальности использует `Lookup`, а не `Dictionary`

Наша реализация: `Dictionary` ищет группу по ключу, `List` хранит порядок групп. Настоящий `GroupBy` строит `Lookup<TKey, TElement>` — собственную хеш-таблицу, которая решает обе задачи сразу. Отличий два, и оба существенные.

**`null` как ключ.** `Dictionary` не принимает `null`-ключ вообще — ни у ссылочных типов (`string`, классы), ни у `Nullable<T>` (`int?`): любая операция с таким ключом бросает `ArgumentNullException`. Поэтому наш `MyGroupBy` падает, как только `keySelector` вернёт `null`. `Lookup` для `null` использует хеш `0` и обычное сравнение, так что `null` становится полноценным ключом со своей группой.

```csharp
var names = new[] { "Anna", null, "Boris", null };

// ✅ Стандартный GroupBy: null — обычный ключ
foreach (var g in names.GroupBy(n => n))
    Console.WriteLine($"[{g.Key ?? "null"}] x{g.Count()}");
// [Anna] x1
// [null] x2
// [Boris] x1

// ❌ Наш MyGroupBy: ArgumentNullException — Dictionary не принимает null-ключ
names.MyGroupBy(n => n).MyToList();
```

**Гарантированный порядок.** Для `GroupBy` порядок записан в документации: группы идут в порядке первого появления ключа в источнике, а элементы внутри группы — в исходном порядке. У `Dictionary` порядок обхода официально не определён: пока ничего не удаляли, он обычно совпадает с порядком добавления, но это деталь реализации, а не контракт. Поэтому в нашем коде порядок держит отдельный `List`.

> ⚠️ **Практический вывод:** не клади потенциально `null`-ключи в `Dictionary` напрямую. Используй стандартный `GroupBy`/`ToLookup`, подменяй `null` заглушкой (`x ?? "<null>"`) или отфильтруй `null` заранее.

---

### 2. `OrderBy`: кэш ключей, индексы и частичная сортировка

Хорошая новость: главный приём из Части 4 — сортировать **индексы** и при равных ключах сравнивать индексы — настоящий LINQ использует тоже. В `EnumerableSorter` это буквально строка `index1 - index2` с комментарием «ensure stability of sort». Отличий три:

1. **Ключи считаются один раз.** Сначала вычисляется ключ каждого элемента и складывается в массив, потом сортируется карта индексов `int[]`. Наш `MyThenBy` (Часть 10) ради простоты вызывает `keySelector` на каждое сравнение.
2. **Не каждый `OrderBy` — полная сортировка.** `OrderBy(...).First()` и `Last()` находят нужный элемент за один проход, без сортировки. `OrderBy(...).Take(k)`, `Skip(n)` и `ElementAt(n)` используют частичную сортировку (в коде — `PartialQuickSort` и `QuickSelect`): зачем упорядочивать то, что всё равно отбросят.
3. **`ThenBy` — это цепочка сортировщиков.** Каждый уровень хранит свой массив ключей: сравнение идёт по первому ключу, при равенстве управление передаётся следующему уровню, и только в самом конце решают индексы.

```
Наш подход (Части 4 и 10):          Настоящий LINQ:
  buffer + карта индексов             buffer + массивы ключей (по одному на уровень) + карта индексов
  keySelector — на каждое сравнение   keySelector — один раз на элемент
  всегда полная сортировка            First/Last — один проход, Take/Skip/ElementAt — частичная сортировка
```

> 🧠 **Вывод для практики:** `OrderBy(x => x.Date).First()` — это не O(n log n), а один проход. Пиши то, что хочешь получить, — и библиотека сама выберет путь. А вот `OrderBy(...).ToList()[0]` уже сортирует всё.

---

### 3. Быстрые пути: проверка типа источника и SIMD

Настоящий LINQ не слепо обходит `IEnumerable<T>`: сначала он выясняет, что ему подсунули, и выбирает кратчайший путь.

- **`Count()` без предиката.** Проверяет `ICollection<T>` (массив, `List<T>`, `HashSet<T>`...), потом внутренние итераторы LINQ, которые знают свой размер, потом необобщённый `ICollection` — и берёт готовое число за O(1). Только если ничего не подошло, считает в цикле. Поэтому `myList.Count()` не обходит список, хотя это вызов LINQ-метода, а не свойства.
- **`Sum()` для `int`/`long`, а также `Min`, `Max`, `Average`.** Если источник — `T[]` или `List<T>`, LINQ получает `ReadOnlySpan<T>` (для `List<T>` — через `CollectionsMarshal.AsSpan`) и обрабатывает числа блоками через `Vector<T>`. Это **SIMD**: одна инструкция процессора работает сразу с несколькими элементами. Векторизованные `Min`/`Max`/`Average` для `int` и `long` появились в .NET 7, `Sum` — в .NET 8.

```csharp
int[] data = GetBigArray();

int a = data.Sum();                   // массив → span → SIMD (.NET 8+)
int b = data.Sum(x => x);             // с селектором — обычный цикл с вызовом делегата
int c = data.Where(x => x > 0).Sum(); // источник — итератор, а не массив → обычный цикл
```

> ⚠️ **SIMD не меняет асимптотику.** Это не «O(n/8)», а тот же O(n) с меньшей константой. И работает он только там, где LINQ видит «голый» массив или список чисел: стоит поставить перед `Sum()` оператор `Where` или `Select`, как источником станет итератор — и вернётся обычный цикл.

> 🧠 **Практический совет:** если пишешь горячий путь — проверяй, не является ли `IEnumerable<T>` конкретным типом (`T[]`, `List<T>`), и работай с ним напрямую. LINQ это делает сам, но ты должен понимать почему.

---

### 4. Следующий уровень: `IAsyncEnumerable<T>` и `await foreach`

Всё, что мы разобрали — синхронный мир. Но современный .NET давно работает с асинхронными потоками данных.

`IAsyncEnumerable<T>` — это логичное расширение той же идеи на async/await:

```csharp
// Синхронный итератор — то, что мы строили
IEnumerable<T>      // GetEnumerator() → IEnumerator<T> → MoveNext()

// Асинхронный итератор — та же механика, но с await
IAsyncEnumerable<T> // GetAsyncEnumerator() → IAsyncEnumerator<T> → MoveNextAsync()
```

```csharp
// Асинхронный генератор — yield return работает точно так же
public async IAsyncEnumerable<int> ReadFromDatabaseAsync()
{
    await using var reader = await db.ExecuteReaderAsync(sql);
    while (await reader.ReadAsync())
        yield return reader.GetInt32(0); // строки приходят по одной, без буферизации
}

// Потребление
await foreach (var item in ReadFromDatabaseAsync())
    Console.WriteLine(item);
```

Компилятор генерирует **асинхронный конечный автомат** — тот же паттерн, что и для `yield return`, но каждый переход между состояниями может быть `await`-точкой.

### Две вещи, без которых `IAsyncEnumerable` нельзя использовать в проде

**1. `CancellationToken` и атрибут `[EnumeratorCancellation]`**

Без поддержки отмены асинхронный стрим нельзя прервать, пока он ждёт очередную порцию данных. Если соединение с БД зависло — метод будет висеть вечно.

```csharp
// reader — условный DbDataReader, открытый так же, как в примере выше

// ❌ Нельзя отменить — опасно в продакшене
public async IAsyncEnumerable<int> ReadRowsAsync()
{
    while (await reader.ReadAsync())
        yield return reader.GetInt32(0);
}

// ✅ Правильно: принимаем токен + атрибут [EnumeratorCancellation]
// Атрибут позволяет передать токен через WithCancellation() снаружи,
// а не только через прямой параметр метода
public async IAsyncEnumerable<int> ReadRowsAsync(
    [EnumeratorCancellation] CancellationToken ct = default)
{
    while (await reader.ReadAsync(ct))
    {
        ct.ThrowIfCancellationRequested();
        yield return reader.GetInt32(0);
    }
}

// Потребление с отменой через 5 секунд
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await foreach (var item in ReadRowsAsync().WithCancellation(cts.Token))
    Console.WriteLine(item);
```

**2. LINQ-операторы для `IAsyncEnumerable<T>`**

Обычные `Enumerable.Where/Select/...` с `IAsyncEnumerable<T>` не работают: этот интерфейс не наследуется от `IEnumerable<T>`. Асинхронный LINQ — отдельный набор методов (`Where`, `Select`, `ToListAsync`, `FirstOrDefaultAsync`...), и где он живёт, зависит от версии .NET:

| Версия | Откуда брать async-LINQ |
|---|---|
| .NET 10 и новее | Встроен в платформу (`System.Linq.AsyncEnumerable`), ничего подключать не нужно |
| .NET 9 и старее | NuGet-пакет `System.Linq.Async` (`dotnet add package System.Linq.Async`) |

```csharp
// ❌ Не скомпилируется: синхронного ToList() у IAsyncEnumerable нет ни в одной версии
var result = ReadRowsAsync().Where(x => x > 10).ToList();

// ✅ Асинхронная материализация
var result = await ReadRowsAsync()
    .Where(x => x > 10)       // IAsyncEnumerable<int>
    .ToListAsync();           // материализация асинхронно
```

> ⚠️ **Миграция на .NET 10.** Если в проекте остался пакет `System.Linq.Async`, вызовы `Where`/`Select`/`ToListAsync` могут стать неоднозначными: теперь есть две реализации одних и тех же методов. Тогда ссылку на пакет нужно убрать (при мультитаргетинге — оставить только для старых целевых платформ через `Condition`).

Практическое применение: стриминг данных из БД, обработка очередей сообщений, HTTP-стриминг (Server-Sent Events), работа с файлами построчно.

| | `IEnumerable<T>` | `IQueryable<T>` | `IAsyncEnumerable<T>` |
|---|---|---|---|
| Данные | В памяти | В БД / внешнем источнике | Поток (БД, сеть, файл) |
| Выполнение | Синхронное | Откладывается до материализации | Асинхронное, поэлементное |
| Отмена | — | `CancellationToken` в async-методах провайдера (`ToListAsync(ct)` в EF Core) | `[EnumeratorCancellation]` + `CancellationToken` |
| LINQ-методы | Встроены | Встроены | Встроены с .NET 10; раньше — пакет `System.Linq.Async` |
| Когда использовать | Коллекции в RAM | EF Core, любой провайдер | Стриминг, очереди, I/O |

---

## 🐛 Часть 15. Реальные баги: как LINQ стреляет в ногу

> 🟡 **Уровень: Senior**

Правила — хорошо. Но лучше запоминаются конкретные факапы. Вот классика, которая встречается в продакшн-коде. (Ещё одну — `ToList()` раньше времени, когда фильтр уезжает из SQL в память, — я уже разобрал в Части 8, Правило 1, поэтому здесь её нет.)

---

### Баг 1: двойная итерация

```csharp
// ❌ ПЛОХО — запрос выполнится ДВАЖДЫ
var query = items.Where(x => x.IsActive);

int count = query.Count();   // первый проход по items (полный)
var first = query.First();   // второй проход по items (до первого подходящего)
```

Каждый вызов на `IEnumerable<T>` запускает конвейер заново с нуля. Если `items` — это результат сетевого запроса или чтения файла, это два запроса/чтения вместо одного. Хуже того, результаты проходов могут не совпасть: источник мог измениться между ними, а итератор с побочными эффектами (логирование, случайные числа) выполнит их дважды.

```csharp
// ✅ Материализуй один раз, используй сколько угодно
var list = items.Where(x => x.IsActive).ToList();

int count = list.Count;   // O(1), без итерации
var first = list[0];      // O(1), без итерации
```

> ⚠️ Особенно опасно с `IQueryable`: двойной вызов означает два SQL-запроса к базе.

---

### Баг 2: N+1 запросов (смерть ORM-приложения)

Один запрос за списком — и ещё по одному запросу на каждый элемент этого списка. Самая частая причина — ленивая загрузка (lazy loading) связанных данных.

```csharp
// ❌ КАТАСТРОФА (при включённой ленивой загрузке): 1 запрос на Users + N запросов на Orders
foreach (var user in db.Users.Where(u => u.IsActive).ToList())
{
    // Первое обращение к user.Orders — отдельный SELECT к БД!
    Console.WriteLine($"{user.Name}: {user.Orders.Count} заказов");
}
// При 1000 пользователей → 1001 SQL-запрос
```

> ⚠️ В EF Core ленивая загрузка по умолчанию **выключена**: её включают явно (`UseLazyLoadingProxies()` из пакета `Microsoft.EntityFrameworkCore.Proxies` и `virtual`-навигации либо внедрение `ILazyLoader`). Без неё `user.Orders` просто не загрузится: будет `null` или пустая коллекция, и в отчёте тихо окажутся неверные цифры.

А N+1 легко получить и без ленивой загрузки — запросом внутри цикла:

```csharp
// ❌ Тот же эффект вручную: SELECT COUNT(*) на каждой итерации
foreach (var user in users)
    user.OrderCount = db.Orders.Count(o => o.UserId == user.Id);
```

Лечение — попросить у БД всё нужное сразу:

```csharp
// ✅ Eager loading: связанные данные приезжают тем же запросом
var users = db.Users
    .Where(u => u.IsActive)
    .Include(u => u.Orders)  // JOIN в SQL
    .ToList();

foreach (var user in users)
    Console.WriteLine($"{user.Name}: {user.Orders.Count} заказов");
// Итого: 1 SQL-запрос
```

Если нужны только цифры, проецируй сразу: EF построит один запрос и не потащит в память сами заказы:

```csharp
// ✅ Ещё лучше: проекция, COUNT считается прямо в SQL
var result = db.Users
    .Where(u => u.IsActive)
    .Select(u => new { u.Name, OrderCount = u.Orders.Count() })
    .ToList();
// → SELECT Name, (SELECT COUNT(*) FROM Orders WHERE UserId = ...) FROM Users WHERE IsActive = 1
```

---

### Баг 3: бесконечный итератор без материализации

```csharp
// Генератор бесконечной последовательности
public static IEnumerable<int> Naturals()
{
    int n = 0;
    while (true) yield return n++;
}

// ❌ ToList пытается вычитать бесконечность: копит элементы, пока не кончится память (OutOfMemoryException)
var all = Naturals().ToList();

// ✅ Бери ровно столько, сколько нужно, и материализуй
var first10 = Naturals().Take(10).ToList();  // [0, 1, 2, ..., 9]
```

---

### Чеклист для code review: пять вопросов к любому LINQ-запросу

Когда видишь LINQ в чужом (или своём) коде — спроси:

```
1. ГДЕ выполняется?
   → В памяти (IEnumerable), в БД (IQueryable) или в потоке (IAsyncEnumerable)?

2. КОГДА выполняется?
   → Сейчас (ToList, Count, Any) или при итерации (Where, Select)?

3. СКОЛЬКО РАЗ выполняется?
   → Нет ли двойной итерации по одному и тому же запросу?

4. Нет ли лишней материализации?
   → ToList() стоит до Where/Select? Если да — почему?

5. Нет ли N+1?
   → Есть ли обращение к навигационным свойствам (или запрос к БД) внутри foreach?
```

Эти пять вопросов закрывают подавляющее большинство проблем с производительностью LINQ, которые я видел в реальных проектах.

---

## 🥷 Часть 16. Zero-Allocation LINQ: чёрный пояс

> 🔴 **Уровень: Hardcore**

Всё, что написано до этого — уже за пределами большинства туториалов. Но есть ещё один уровень: высоконагруженные движки и библиотеки (например, `NetFabric.Hyperlinq` и `StructLinq`) пишут LINQ, который почти не трогает кучу.

Проблема стандартного LINQ в двух вещах:

```csharp
items.Where(x => x.Age > 18)
//    ↑     ↑
//    │     └─ Func<T, bool> — делегат: объект в куче, вызов через него JIT обычно не инлайнит
//    └─ объект-итератор (WhereIterator и его собратья) — тоже в куче
```

Убираем оба источника аллокаций.

---

### Секрет 1: утиная типизация в `foreach`

Мало кто знает, но `foreach` вообще не требует `IEnumerable<T>`. Компилятор ищет у объекта публичный метод `GetEnumerator()`, возвращающий что-то с `bool MoveNext()` и свойством `Current`. Интерфейс необязателен.

Это значит, что итератор можно реализовать на `ref struct` — структуре, которая живёт **строго на стеке** и никогда не попадает в кучу.

---

### Секрет 2: структурные предикаты вместо делегатов

`Func<T, bool>` — это объект-делегат в куче, а вызов через делегат непрямой, и JIT обычно не может его заинлайнить. Вместо него передаём **структуру**, реализующую интерфейс предиката. Для типов-значений JIT компилирует отдельную специализацию дженерика под каждый конкретный тип, поэтому вызов `_predicate.Invoke(...)` становится прямым (не через интерфейс) и **инлайнится** — `Invoke` превращается в обычный `if`. Ограничение `where TPredicate : struct` как раз гарантирует, что в качестве предиката придёт тип-значение, а не класс.

---

### Полная реализация

```csharp
// ─── Шаг 1: интерфейс предиката (без делегатов) ─────────────────────────────
public interface IValuePredicate<T>
{
    bool Invoke(T item);
}

// ─── Шаг 2: итератор на ref struct (живёт только на стеке) ──────────────────
public ref struct SpanWhereEnumerator<T, TPredicate>
    where TPredicate : struct, IValuePredicate<T>
{
    private ReadOnlySpan<T> _source;
    private TPredicate _predicate;
    private int _index;

    public SpanWhereEnumerator(ReadOnlySpan<T> source, TPredicate predicate)
    {
        _source    = source;
        _predicate = predicate;
        _index     = -1;
    }

    public bool MoveNext()
    {
        while (++_index < _source.Length)
            if (_predicate.Invoke(_source[_index]))
                return true;
        return false;
    }

    // ref readonly — не копируем тяжёлую структуру при получении значения
    public readonly ref readonly T Current => ref _source[_index];
}

// ─── Шаг 3: фасад-обёртка (тоже ref struct, тоже на стеке) ──────────────────
public readonly ref struct SpanWhereEnumerable<T, TPredicate>
    where TPredicate : struct, IValuePredicate<T>
{
    private readonly ReadOnlySpan<T> _source;
    private readonly TPredicate _predicate;

    public SpanWhereEnumerable(ReadOnlySpan<T> source, TPredicate predicate)
    {
        _source    = source;
        _predicate = predicate;
    }

    // foreach ищет именно этот метод — интерфейс не нужен
    public SpanWhereEnumerator<T, TPredicate> GetEnumerator() =>
        new SpanWhereEnumerator<T, TPredicate>(_source, _predicate);
}

// ─── Шаг 4: метод расширения ─────────────────────────────────────────────────
public static class ZeroAllocLinq
{
    public static SpanWhereEnumerable<T, TPredicate> WhereZeroAlloc<T, TPredicate>(
        this ReadOnlySpan<T> source,
        TPredicate predicate)
        where TPredicate : struct, IValuePredicate<T>
    {
        return new SpanWhereEnumerable<T, TPredicate>(source, predicate);
    }

    // Перегрузка для массивов: users.WhereZeroAlloc(...) работает без ручного приведения к span
    public static SpanWhereEnumerable<T, TPredicate> WhereZeroAlloc<T, TPredicate>(
        this T[] source,
        TPredicate predicate)
        where TPredicate : struct, IValuePredicate<T>
    {
        return new SpanWhereEnumerable<T, TPredicate>(source, predicate);
    }
}
```

---

### Использование

Предикат описываем как структуру — один раз, zero overhead:

```csharp
public struct IsAdultPredicate : IValuePredicate<User>
{
    public bool Invoke(User item) => item.Age > 18;
}
```

```csharp
User[] users = GetUsers();

// Ни одной аллокации. GC отдыхает.
foreach (ref readonly var user in users.WhereZeroAlloc(new IsAdultPredicate()))
{
    Console.WriteLine(user.Name);
}

// Для List<T> и других источников — сначала получи ReadOnlySpan<T>
// (CollectionsMarshal живёт в System.Runtime.InteropServices)
List<User> usersList = GetUsersList();
ReadOnlySpan<User> span = CollectionsMarshal.AsSpan(usersList);
foreach (ref readonly var user in span.WhereZeroAlloc(new IsAdultPredicate()))
{
    Console.WriteLine(user.Name);
}
```

> ⚠️ **Версия языка.** Запись `users.AsSpan().WhereZeroAlloc(...)` скомпилируется только в C# 14 (.NET 10 SDK): `Span<T>` → `ReadOnlySpan<T>` — это пользовательское неявное преобразование, а до C# 14 оно не применялось к `this`-параметру метода расширения. Если не уверен в версии языка, присвой `ReadOnlySpan<T>` переменной (как выше) или используй перегрузку для массива.

---

### Что происходит под капотом

```
users (User[]) → ReadOnlySpan<User>
  └─ «окно» в массив — 0 аллокаций, просто указатель + длина

new IsAdultPredicate()
  └─ создаётся на стеке, уничтожается при выходе из метода

WhereZeroAlloc(...)
  └─ возвращает ref struct — тоже на стеке

Вызов _predicate.Invoke(item)
  └─ TPredicate — struct → JIT делает специализацию под этот тип: вызов прямой, не через интерфейс
  └─ метод инлайнится → компилируется в обычный if (user.Age > 18)

ref readonly var user
  └─ ссылка на элемент в массиве — элемент не копируется (важно, если User — большая структура)
```

---

### Сравнение подходов

| | Стандартный LINQ | `for` / `foreach` | Zero-Alloc LINQ |
|---|---|---|---|
| Читаемость | ✅ Отличная | ⚠️ Многословно | ✅ Хорошая |
| Аллокации кучи | ❌ Итератор (+ замыкание, если есть захват) | ✅ Нет | ✅ Нет |
| Давление на GC | ❌ Есть | ✅ Нет | ✅ Нет |
| Инлайнинг предиката | ❌ Обычно нет (вызов через делегат) | ✅ Да | ✅ Да (специализация под struct) |
| Работает с `Span<T>` | ❌ Нет | ✅ Да | ✅ Да |
| Применение | Бизнес-логика | Hot path, простые условия | Hot path + сложная логика |

---

### Ограничения `ref struct`

За zero-allocation приходится платить:

```csharp
public struct PositivePredicate : IValuePredicate<int>
{
    public bool Invoke(int x) => x > 0;
}

// ❌ ref struct нельзя хранить в куче — например, в поле класса
class MyClass
{
    SpanWhereEnumerable<int, PositivePredicate> _query; // ошибка компиляции
}

// ❌ ref struct нельзя боксировать (привести к object или интерфейсу)
IEnumerable<int> e = query; // ошибка компиляции

// ❌ ref struct нельзя держать «через await»
async Task ProcessAsync(int[] array)
{
    ReadOnlySpan<int> span = array;
    var q = span.WhereZeroAlloc(new PositivePredicate());
    await Task.Delay(1);
    foreach (var x in q) { } // ошибка компиляции (CS4007): q пришлось бы хранить в async-автомате, а он живёт в куче
}

// ✅ Работа со ref struct — в обычных синхронных методах
void Process(ReadOnlySpan<int> data)
{
    foreach (ref readonly var item in data.WhereZeroAlloc(new PositivePredicate()))
        Handle(item);
}

// ✅ А async-метод просто вызывает синхронный
async Task ProcessAsync(int[] array)
{
    await Task.Delay(1);
    Process(array);
}
```

> ℹ️ **Про async и версии C#.** До C# 13 в async-методах нельзя было объявлять локальные переменные типа `ref struct` (например, `ReadOnlySpan<int>`) и `ref`-локальные вообще: компилятор отвечал ошибкой CS9202. Начиная с C# 13 можно — пока такая переменная не живёт через `await` (иначе — CS4007, как в примере выше). Параметром async-метода `ref struct` быть не может ни в одной версии (CS4012).

> 🧠 **Когда применять:** игровые движки, парсеры бинарных протоколов, обработка аудио/видео в реальном времени, любой горячий путь с **измеренным** давлением на GC.
>
> ⛔ **Жёсткое правило:** если ты не запустил профилировщик и не увидел цифры — тебе это не нужно. Zero-alloc LINQ усложняет код, плохо дружит с async, запрещает хранение в полях. Цена должна быть оправдана данными из `dotnet-trace` или BenchmarkDotNet, а не интуицией. Фанатизм по аллокациям в CRUD-приложении — это архитектурный долг, не оптимизация.

---

## 📝 Глоссарий

| Термин | Определение |
|---|---|
| **Deferred Execution** | Запрос — это лишь план. Данные не трогаются до начала перебора |
| **Immediate Execution** | Метод вычисляет результат немедленно (`ToList`, `Count`, `Any`) |
| **Short-circuiting** | Остановка перебора, как только цель достигнута (`Any`, `First`) |
| **Materialization** | Превращение ленивого запроса в конкретную коллекцию в памяти |
| **Greedy (жадный) итератор** | Снаружи ленивый, но при первом `MoveNext()` вычитывает весь источник (`OrderBy`, `GroupBy`) |
| **Projection** | Преобразование типа элементов через `Select` |
| **Predicate** | Функция-условие `Func<T, bool>` |
| **State Machine** | Скрытый класс, который компилятор создаёт из метода с `yield return` |
| **Closure (замыкание)** | Лямбда, захватившая внешнюю переменную: компилятор прячет её в скрытом классе в куче |
| **Expression Tree** | Данные о коде — «чертёж» лямбды, который можно проанализировать |
| **DSL** | Язык предметной области. `IQueryable` — это DSL построения запроса: ты описываешь, что нужно, а провайдер переводит это на язык источника |
| **Client Evaluation** | Часть запроса, которую провайдер не смог перевести в SQL и выполняет в памяти приложения. В EF Core допустима только в финальном `Select` |
| **N+1** | Один запрос за списком плюс по запросу на каждый его элемент |
| **Fail Fast** | Принцип: падай немедленно и в нужном месте, а не где-то в глубине цикла |
| **Stable Sort** | Сортировка, при которой элементы с равными ключами сохраняют исходный порядок |
| **Lookup** | Структура, на которой работает настоящий `GroupBy`/`ToLookup`: допускает `null`-ключ и гарантирует порядок первого появления ключей |
| **Covariance (`out T`)** | `IEnumerable<string>` можно использовать там, где ожидается `IEnumerable<object>` (только для ссылочных типов) |
| **Contravariance (`in T`)** | `IComparer<object>` можно использовать там, где ожидается `IComparer<string>` |
| **Invariance** | Параметр типа без `in`/`out`: `List<string>` — не `List<object>` |
| **SIMD** | Одна инструкция процессора обрабатывает сразу несколько элементов; в LINQ так считаются `Sum`/`Min`/`Max`/`Average` по массивам и спискам базовых числовых типов (только перегрузки без селектора) |
| **`ref struct`** | Структура, которая может жить только на стеке (`Span<T>` и наши zero-alloc итераторы): её нельзя хранить в поле класса и боксировать |
| **Inlining / девиртуализация** | Оптимизации JIT: подстановка тела метода в место вызова и замена непрямого вызова прямым |

---

> **Ментальная модель сеньора.** Каждый раз, когда видишь LINQ, прогоняй те самые пять вопросов из чеклиста в Части 15: **где** выполняется, **когда**, **сколько раз**, **нет ли лишней материализации** и **нет ли N+1**.
>
> Это не чеклист. Это рефлекс. Когда он срабатывает автоматически — ты думаешь как инженер, а не как пользователь API.

> **Напутствие.** Теперь когда ты видишь `items.Where(...).Select(...).ToList()` — ты видишь не строчку кода.  
> Ты видишь конечный автомат, конвейер итераторов, момент материализации.  
> Когда пишешь `.GroupBy(...).OrderBy(...).ThenBy(...)` — видишь группы с поиском по ключу, цепочку обёрнутых компараторов и одну сортировку индексов со стабилизатором.  
> Когда подключаешь EF Core — видишь DSL построения запроса, провайдер и точку, где лямбда перестаёт быть кодом и становится SQL. И знаешь, где провайдер сдастся с `InvalidOperationException`.  
> Когда пишешь `await foreach` — помнишь про `[EnumeratorCancellation]` и про то, что async-LINQ живёт отдельно: пакет `System.Linq.Async` до .NET 10, встроенный в платформу — начиная с .NET 10.  
> А когда кто-то предлагает zero-alloc LINQ без профилировщика в руках — знаешь, что сказать.
>
> Инструмент знают все. Механику знают единицы. Ты теперь из вторых.

#dotnet #csharp #linq #performance #архитектура
