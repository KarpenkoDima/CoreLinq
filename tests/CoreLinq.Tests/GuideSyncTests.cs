using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CoreLinq.Tests
{
    /// <summary>
    /// Код в методичке и код в src/CoreLinq/MyLinq.cs — это одно и то же.
    /// Тест не даёт им разойтись: правишь одно — правь и другое.
    /// </summary>
    public class GuideSyncTests
    {
        [Fact]
        public void Final_code_in_Part_13_of_the_guide_equals_src_MyLinq_cs()
        {
            string root = FindRepositoryRoot();
            string guide = File.ReadAllText(Path.Combine(root, "docs", "LINQ_Pod_Kapotom.md"));
            string source = File.ReadAllText(Path.Combine(root, "src", "CoreLinq", "MyLinq.cs"));

            Assert.Equal(Normalize(source), Normalize(ExtractPart13Code(guide)));
        }

        private static string ExtractPart13Code(string guide)
        {
            string[] lines = guide.Replace("\r\n", "\n").Split('\n');

            int heading = Array.FindIndex(lines, line => Regex.IsMatch(line, @"^## .*Часть 13\."));
            Assert.True(heading >= 0, "В методичке не найден заголовок Части 13");

            int start = Array.FindIndex(lines, heading + 1, line => line.StartsWith("```csharp", StringComparison.Ordinal));
            Assert.True(start >= 0, "После заголовка Части 13 нет блока кода");

            int end = Array.FindIndex(lines, start + 1, line => line.StartsWith("```", StringComparison.Ordinal));
            Assert.True(end > start, "Блок кода Части 13 не закрыт");

            return string.Join("\n", lines, start + 1, end - start - 1);
        }

        private static string Normalize(string text)
        {
            return text.Replace("\r\n", "\n").TrimEnd();
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CoreLinq.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }
    }
}
