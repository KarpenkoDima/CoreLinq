using System.Collections.Generic;

namespace CoreLinq.EfCore.Experiments.Model
{
    /// <summary>Сущность из примеров методички (Части 7, 8 и 15): пользователь с заказами.</summary>
    public class User
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public int Age { get; set; }

        public bool IsActive { get; set; }

        // virtual нужен для ленивых прокси (UseLazyLoadingProxies). Без прокси модификатор ни на что не влияет
        public virtual ICollection<Order> Orders { get; set; } = new List<Order>();
    }
}
