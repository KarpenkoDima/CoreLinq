using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreLinq.EfCore.Experiments.Infrastructure;
using CoreLinq.EfCore.Experiments.Model;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreLinq.EfCore.Experiments
{
    /// <summary>
    /// Часть 14 методички: отмена и стриминг в EF Core.
    /// Получатели async-методов здесь нарочно объявлены как IQueryable&lt;T&gt;: на .NET 10 у типа, который реализует
    /// и IQueryable&lt;T&gt;, и IAsyncEnumerable&lt;T&gt;, вызов ToListAsync() может оказаться неоднозначным.
    /// </summary>
    public class AsyncTests
    {
        [Fact]
        public async Task ToListAsync_works_and_returns_the_same_rows_as_ToList()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            IQueryable<User> query = db.Users.Where(u => u.IsActive).OrderBy(u => u.Id);

            List<User> viaSync = query.ToList();
            List<User> viaAsync = await query.ToListAsync();

            Assert.Equal(viaSync.Select(u => u.Name).ToList(), viaAsync.Select(u => u.Name).ToList());
            Assert.Equal(2, database.Sent.Count);
        }

        [Fact]
        public async Task ToListAsync_with_an_already_cancelled_token_throws_OperationCanceledException()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();
            using CancellationTokenSource cts = new();
            cts.Cancel();

            IQueryable<User> query = db.Users.Where(u => u.IsActive);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.ToListAsync(cts.Token));
        }

        [Fact]
        public async Task AsAsyncEnumerable_streams_rows_one_by_one()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            IQueryable<User> query = db.Users.OrderBy(u => u.Id);
            List<string> names = new();

            await foreach (User user in query.AsAsyncEnumerable())
            {
                names.Add(user.Name);
            }

            Assert.Equal(new[] { "Anna", "Boris", "Clara", "Dima", "Elena" }, names);
            Assert.Single(database.Sent);
        }

        [Fact]
        public async Task WithCancellation_flows_the_token_into_the_streaming_query()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();
            using CancellationTokenSource cts = new();

            IQueryable<User> query = db.Users.OrderBy(u => u.Id);
            List<string> seen = new();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (User user in query.AsAsyncEnumerable().WithCancellation(cts.Token))
                {
                    seen.Add(user.Name);
                    cts.Cancel(); // отменяем после первой же строки
                }
            });

            Assert.Equal(new[] { "Anna" }, seen);
        }
    }
}
