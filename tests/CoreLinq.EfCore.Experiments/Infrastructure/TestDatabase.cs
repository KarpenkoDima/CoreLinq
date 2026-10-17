using System;
using System.Collections.Generic;
using CoreLinq.EfCore.Experiments.Model;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CoreLinq.EfCore.Experiments.Infrastructure
{
    /// <summary>
    /// Чистая SQLite-база в памяти с тестовыми данными и перехватчиком SQL.
    /// SQLite — настоящий реляционный провайдер: он переводит LINQ в SQL и отказывается переводить то,
    /// что не умеет, так же, как это делает боевая СУБД. Провайдер InMemory так не умеет, поэтому здесь он не используется.
    ///
    /// Данные (одни и те же в каждом тесте):
    ///   Id  Name   Age  IsActive  Orders
    ///   1   Anna   17   да        0
    ///   2   Boris  25   да        3
    ///   3   Clara  34   нет       1
    ///   4   Dima   18   да        2
    ///   5   Elena  52   да        1
    /// </summary>
    public sealed class TestDatabase : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly bool _lazyLoading;
        private readonly Action<string>? _log;

        public TestDatabase(bool lazyLoading = false, Action<string>? log = null)
        {
            _lazyLoading = lazyLoading;
            _log = log;

            // База в памяти живёт, пока открыто соединение, поэтому держим его открытым всё время жизни TestDatabase
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            using (ShopContext seedContext = BuildContext(lazyLoading: false, log: null))
            {
                seedContext.Database.EnsureCreated();
                Seed(seedContext);
            }

            // Подготовка базы в счёт не идёт: тесты считают только свои запросы
            Recorder.Clear();
        }

        public CommandRecorder Recorder { get; } = new();

        /// <summary>Все команды, которые ушли в базу после подготовки (или после последнего ClearSent).</summary>
        public IReadOnlyList<RecordedCommand> Sent
        {
            get
            {
                return Recorder.Commands;
            }
        }

        public void ClearSent()
        {
            Recorder.Clear();
        }

        public ShopContext CreateContext()
        {
            return BuildContext(_lazyLoading, _log);
        }

        public void Dispose()
        {
            _connection.Dispose();
        }

        private ShopContext BuildContext(bool lazyLoading, Action<string>? log)
        {
            DbContextOptionsBuilder<ShopContext> builder = new();
            builder.UseSqlite(_connection);
            builder.AddInterceptors(Recorder);

            if (lazyLoading)
            {
                builder.UseLazyLoadingProxies();
            }

            if (log != null)
            {
                builder.LogTo(log, (eventId, level) => eventId.Id == RelationalEventId.CommandExecuted.Id);
            }

            return new ShopContext(builder.Options);
        }

        private static void Seed(ShopContext context)
        {
            User anna = new() { Name = "Anna", Age = 17, IsActive = true };
            User boris = new() { Name = "Boris", Age = 25, IsActive = true };
            User clara = new() { Name = "Clara", Age = 34, IsActive = false };
            User dima = new() { Name = "Dima", Age = 18, IsActive = true };
            User elena = new() { Name = "Elena", Age = 52, IsActive = true };

            boris.Orders.Add(new Order { AmountCents = 1000 });
            boris.Orders.Add(new Order { AmountCents = 2500 });
            boris.Orders.Add(new Order { AmountCents = 400 });
            clara.Orders.Add(new Order { AmountCents = 700 });
            dima.Orders.Add(new Order { AmountCents = 1500 });
            dima.Orders.Add(new Order { AmountCents = 900 });
            elena.Orders.Add(new Order { AmountCents = 3000 });

            context.Users.AddRange(anna, boris, clara, dima, elena);
            context.SaveChanges();
        }
    }
}
