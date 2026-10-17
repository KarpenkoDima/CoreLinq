namespace CoreLinq.Tests
{
    // Сотрудник из данных к боевым тестам (Часть 12 методички)
    public sealed record Employee(string Name, string Dept, int Salary, int Age);

    public static class TestData
    {
        public static readonly Employee[] Employees =
        {
            new("Anna",  "IT", 90000,  30),
            new("Boris", "HR", 60000,  45),
            new("Clara", "IT", 90000,  25),
            new("Dima",  "HR", 75000,  35),
            new("Elena", "IT", 110000, 28),
            new("Fedor", "HR", 60000,  45),
            new("Galya", "IT", 90000,  30),
        };
    }
}
