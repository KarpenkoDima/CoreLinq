using Microsoft.EntityFrameworkCore;

namespace CoreLinq.EfCore.Experiments.Model
{
    public class ShopContext : DbContext
    {
        public ShopContext(DbContextOptions<ShopContext> options) : base(options)
        {
        }

        public DbSet<User> Users => Set<User>();

        public DbSet<Order> Orders => Set<Order>();
    }
}
