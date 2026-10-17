namespace CoreLinq.EfCore.Experiments.Model
{
    public class Order
    {
        public int Id { get; set; }

        public int UserId { get; set; }

        // Сумма в копейках: SQLite не умеет сортировать и агрегировать decimal, а нам это здесь не нужно
        public int AmountCents { get; set; }

        public virtual User User { get; set; } = null!;
    }
}
