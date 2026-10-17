using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
#if NET10_0_OR_GREATER
using System.Linq;
#endif

namespace CoreLinq.Tests
{
    /// <summary>
    /// Часть 14 методички: отмена в IAsyncEnumerable, атрибут [EnumeratorCancellation]
    /// и async-LINQ, встроенный в .NET 10.
    /// </summary>
    public class AsyncStreamTests
    {
        // ✅ Как в методичке: токен + [EnumeratorCancellation]
        private static async IAsyncEnumerable<int> CountForever([EnumeratorCancellation] CancellationToken ct = default)
        {
            int i = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return i++;
            }
        }

        // ❌ Токен-параметр есть, атрибута нет: токен из WithCancellation(...) до метода не дойдёт.
        // Компилятор об этом предупреждает (CS8425) — здесь предупреждение нужно нам самим как демонстрация
#pragma warning disable CS8425
        private static async IAsyncEnumerable<int> CountForeverWithoutAttribute(CancellationToken ct = default)
#pragma warning restore CS8425
        {
            int i = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return i++;
            }
        }

        [Fact]
        public async Task Token_passed_to_WithCancellation_reaches_the_iterator_thanks_to_the_attribute()
        {
            using CancellationTokenSource cts = new();
            List<int> seen = new();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (int item in CountForever().WithCancellation(cts.Token))
                {
                    seen.Add(item);
                    if (item == 2)
                    {
                        cts.Cancel();
                    }
                }
            });

            Assert.Equal(new[] { 0, 1, 2 }, seen);
        }

        [Fact]
        public async Task Token_can_be_passed_directly_as_a_method_argument_too()
        {
            using CancellationTokenSource cts = new();
            List<int> seen = new();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (int item in CountForever(cts.Token))
                {
                    seen.Add(item);
                    if (item == 1)
                    {
                        cts.Cancel();
                    }
                }
            });

            Assert.Equal(new[] { 0, 1 }, seen);
        }

        [Fact]
        public async Task Without_the_attribute_the_token_from_WithCancellation_is_silently_ignored()
        {
            using CancellationTokenSource cts = new();
            List<int> seen = new();

            await foreach (int item in CountForeverWithoutAttribute().WithCancellation(cts.Token))
            {
                seen.Add(item);
                if (item == 2)
                {
                    cts.Cancel();
                }

                if (item == 5)
                {
                    break; // поток бесконечный, останавливаемся сами
                }
            }

            // Токен отменён, но итератор о нём не знает: прервал его только break
            Assert.True(cts.IsCancellationRequested);
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, seen);
        }

#if NET10_0_OR_GREATER
        private static async IAsyncEnumerable<int> Numbers(int count)
        {
            for (int i = 0; i < count; i++)
            {
                await Task.Yield();
                yield return i;
            }
        }

        [Fact]
        public async Task Dotnet10_has_async_LINQ_built_in_and_needs_no_System_Linq_Async_package()
        {
            // В .NET 10 Where и ToListAsync для IAsyncEnumerable<T> живут в System.Linq.AsyncEnumerable (часть платформы)
            List<int> result = await Numbers(15).Where(x => x > 10).ToListAsync();

            Assert.Equal(new[] { 11, 12, 13, 14 }, result);
        }
#endif
    }
}
