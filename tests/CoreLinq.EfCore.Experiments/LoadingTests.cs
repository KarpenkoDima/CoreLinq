using System;
using System.Collections.Generic;
using System.Linq;
using CoreLinq.EfCore.Experiments.Infrastructure;
using CoreLinq.EfCore.Experiments.Model;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace CoreLinq.EfCore.Experiments
{
    /// <summary>
    /// Часть 8 (золотые правила) и Часть 15 (двойная итерация, N+1): сколько запросов уходит в базу
    /// и что именно приезжает в память. Считаем настоящие команды через перехватчик.
    /// </summary>
    public class LoadingTests
    {
        private readonly ITestOutputHelper _output;

        public LoadingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static IEnumerable<User> GetAdultsAsEnumerable(ShopContext db)
        {
            return db.Users.Where(u => u.Age > 18);
        }

        private static IQueryable<User> GetAdultsAsQueryable(ShopContext db)
        {
            return db.Users.Where(u => u.Age > 18);
        }

        [Fact]
        public void Rule1_ToList_too_early_pulls_the_whole_table_into_memory()
        {
            // ❌ ПЛОХО: ToList() раньше Where
            using TestDatabase badDatabase = new();
            using ShopContext badDb = badDatabase.CreateContext();

            List<User> bad = badDb.Users.ToList().Where(u => u.Age > 18).ToList();

            RecordedCommand badCommand = Assert.Single(badDatabase.Sent);
            _output.WriteLine("ToList().Where(...):" + Environment.NewLine + SqlText.Describe(badCommand));
            Assert.DoesNotContain("WHERE", badCommand.Sql);
            Assert.Equal(5, badDb.ChangeTracker.Entries<User>().Count()); // в память приехала вся таблица

            // ✅ ХОРОШО: Where раньше ToList()
            using TestDatabase goodDatabase = new();
            using ShopContext goodDb = goodDatabase.CreateContext();

            List<User> good = goodDb.Users.Where(u => u.Age > 18).ToList();

            RecordedCommand goodCommand = Assert.Single(goodDatabase.Sent);
            _output.WriteLine("Where(...).ToList():" + Environment.NewLine + SqlText.Describe(goodCommand));
            Assert.Contains("WHERE", goodCommand.Sql);
            Assert.Equal(3, goodDb.ChangeTracker.Entries<User>().Count()); // только Boris, Clara, Elena

            // Результат один и тот же, а цена разная
            Assert.Equal(
                bad.Select(u => u.Name).OrderBy(n => n).ToList(),
                good.Select(u => u.Name).OrderBy(n => n).ToList());
        }

        [Fact]
        public void Rule2_Returning_IEnumerable_cuts_the_query_but_IQueryable_keeps_it_open()
        {
            using TestDatabase database = new();

            // ❌ IEnumerable<User>: фильтр, дописанный снаружи, выполнится уже в памяти
            using (ShopContext db = database.CreateContext())
            {
                List<User> viaEnumerable = GetAdultsAsEnumerable(db).Where(u => u.IsActive).ToList();

                RecordedCommand command = Assert.Single(database.Sent);
                _output.WriteLine("IEnumerable:" + Environment.NewLine + SqlText.Describe(command));
                Assert.DoesNotContain("IsActive", SqlText.FromWhere(command.Sql));
                Assert.Equal(2, viaEnumerable.Count); // Boris, Elena
            }

            database.ClearSent();

            // ✅ IQueryable<User>: дописанный снаружи фильтр уходит в SQL
            using (ShopContext db = database.CreateContext())
            {
                List<User> viaQueryable = GetAdultsAsQueryable(db).Where(u => u.IsActive).ToList();

                RecordedCommand command = Assert.Single(database.Sent);
                _output.WriteLine("IQueryable:" + Environment.NewLine + SqlText.Describe(command));
                Assert.Contains("IsActive", SqlText.FromWhere(command.Sql));
                Assert.Equal(2, viaQueryable.Count);
            }
        }

        [Fact]
        public void Bug1_Enumerating_an_IQueryable_twice_sends_two_SQL_queries()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            // ❌ Два обращения к одному запросу — два похода в базу
            IQueryable<User> query = db.Users.Where(u => u.IsActive);
            int count = query.Count();
            User first = query.First();

            Assert.Equal(4, count);
            Assert.NotNull(first);
            Assert.Equal(2, database.Sent.Count);
            Assert.Contains("COUNT", database.Sent[0].Sql);

            // ✅ Материализовали один раз — дальше база не нужна
            database.ClearSent();
            List<User> list = query.ToList();
            int listCount = list.Count;
            User listFirst = list[0];

            Assert.Equal(4, listCount);
            Assert.NotNull(listFirst);
            Assert.Single(database.Sent);
        }

        [Fact]
        public void Bug2_Lazy_loading_is_off_by_default_and_Orders_silently_stay_empty()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            User boris = db.Users.Single(u => u.Name == "Boris");

            // В базе у Бориса три заказа, а в коллекции пусто — и никакой ошибки
            Assert.Empty(boris.Orders);
            Assert.Equal(3, db.Orders.Count(o => o.UserId == boris.Id));
        }

        [Fact]
        public void Bug2_Lazy_loading_proxies_turn_every_first_access_to_Orders_into_a_query()
        {
            using TestDatabase database = new(lazyLoading: true);
            using ShopContext db = database.CreateContext();

            List<User> users = db.Users.Where(u => u.IsActive).ToList(); // 1 запрос
            Assert.Single(database.Sent);
            Assert.IsNotType<User>(users[0]); // это прокси-наследник, а не сам User

            int totalOrders = 0;
            foreach (User user in users)
            {
                totalOrders += user.Orders.Count; // первое обращение к навигации — отдельный SELECT
            }

            _output.WriteLine($"Запросов к базе: {database.Sent.Count} (1 + {users.Count})");
            Assert.Equal(6, totalOrders); // Anna 0 + Boris 3 + Dima 2 + Elena 1
            Assert.Equal(1 + users.Count, database.Sent.Count); // 1 + N
        }

        [Fact]
        public void Bug2_The_same_N_plus_1_appears_without_lazy_loading_when_a_query_sits_inside_a_loop()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            List<User> users = db.Users.Where(u => u.IsActive).ToList(); // 1 запрос

            foreach (User user in users)
            {
                int orderCount = db.Orders.Count(o => o.UserId == user.Id); // SELECT COUNT(*) на каждой итерации
                Assert.True(orderCount >= 0);
            }

            Assert.Equal(1 + users.Count, database.Sent.Count);
        }

        [Fact]
        public void Include_brings_orders_with_the_same_query()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            List<User> users = db.Users
                .Where(u => u.IsActive)
                .Include(u => u.Orders) // JOIN в SQL
                .ToList();

            RecordedCommand command = Assert.Single(database.Sent);
            _output.WriteLine(SqlText.Describe(command));
            Assert.Contains("JOIN", command.Sql);

            int totalOrders = users.Sum(u => u.Orders.Count);
            Assert.Equal(6, totalOrders);
            Assert.Single(database.Sent); // подсчёт заказов в памяти новых запросов не породил
        }

        [Fact]
        public void Projection_counts_orders_in_SQL_and_never_loads_them()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            var result = db.Users
                .Where(u => u.IsActive)
                .OrderBy(u => u.Id)
                .Select(u => new { u.Name, OrderCount = u.Orders.Count() })
                .ToList();

            RecordedCommand command = Assert.Single(database.Sent);
            _output.WriteLine(SqlText.Describe(command));

            Assert.Contains("COUNT(", command.Sql);
            Assert.Empty(db.ChangeTracker.Entries<Order>()); // ни один заказ в память не приехал
            Assert.Equal(new[] { 0, 3, 2, 1 }, result.Select(r => r.OrderCount).ToList()); // Anna, Boris, Dima, Elena
        }
    }
}
