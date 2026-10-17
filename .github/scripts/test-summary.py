#!/usr/bin/env python3
"""Итоги прогона dotnet test для GitHub Actions.

Читает TRX-файлы и делает три вещи:
  1. печатает в лог краткий список тестов;
  2. пишет разметку в $GITHUB_STEP_SUMMARY: таблицу итогов и, для каждого теста, всё, что он вывел
     через ITestOutputHelper (в экспериментах с EF Core это SQL), поэтому страница прогона читается как отчёт;
  3. создаёт аннотации: одну с итогами и по одной на каждый упавший тест (она видна на странице pull request).

Использование: test-summary.py "Заголовок" <каталог с TRX> [<каталог с TRX> ...]
Код возврата всегда 0: пройден тест или нет, решает сам шаг dotnet test.
"""
import glob
import html
import os
import re
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
MAX_ANNOTATION_TEXT = 1500
MAX_ERRORS_AS_ANNOTATIONS = 10


def escape_data(text):
    return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def escape_property(text):
    return escape_data(text).replace(":", "%3A").replace(",", "%2C")


def cut(text, limit):
    text = (text or "").strip()
    return text if len(text) <= limit else text[:limit] + "\n… (обрезано)"


def framework_of(root):
    """Целевой фреймворк берём из пути к тестовой сборке: .../bin/Release/net8.0/Xxx.dll."""
    for unit_test in root.iter("{%s}UnitTest" % NS["t"]):
        match = re.search(r"/(net\d+\.\d+)/", (unit_test.get("storage") or "").replace("\\", "/"))
        if match:
            return match.group(1)
    return "?"


def read_trx(path):
    root = ET.parse(path).getroot()
    results = []
    for node in root.iter("{%s}UnitTestResult" % NS["t"]):
        message = node.findtext("t:Output/t:ErrorInfo/t:Message", default="", namespaces=NS)
        stdout = node.findtext("t:Output/t:StdOut", default="", namespaces=NS)
        results.append(
            {
                "name": node.get("testName") or "?",
                "outcome": node.get("outcome") or "?",
                "duration": node.get("duration") or "",
                "message": message,
                "stdout": stdout,
            }
        )
    return framework_of(root), results


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 0

    title = sys.argv[1]
    files = []
    for directory in sys.argv[2:]:
        files += sorted(glob.glob(os.path.join(directory, "**", "*.trx"), recursive=True))

    if not files:
        print(f"::error title={escape_property(title)}::Не найдено ни одного TRX-файла: тесты не запускались")
        return 0

    summary = [f"## {title}", "", "| Фреймворк | Всего | Прошло | Упало | Пропущено |", "|---|---:|---:|---:|---:|"]
    details = []
    failures = []
    grand_total = 0

    for path in files:
        framework, results = read_trx(path)
        passed = sum(1 for r in results if r["outcome"] == "Passed")
        failed = sum(1 for r in results if r["outcome"] == "Failed")
        skipped = len(results) - passed - failed
        grand_total += len(results)

        summary.append(f"| {framework} | {len(results)} | {passed} | {failed} | {skipped} |")
        print(f"[{title}] {framework}: всего {len(results)}, прошло {passed}, упало {failed}, пропущено {skipped}")
        level = "notice" if failed == 0 and len(results) > 0 else "error"
        print(
            f"::{level} title={escape_property(title + ' ' + framework)}::"
            + escape_data(f"Всего {len(results)}, прошло {passed}, упало {failed}, пропущено {skipped}")
        )

        for result in results:
            mark = {"Passed": "✅", "Failed": "❌"}.get(result["outcome"], "⚪")
            print(f"  {mark} {result['name']}  {result['duration']}")
            if result["outcome"] == "Failed":
                failures.append((framework, result))
            block = [f"<details><summary>{mark} <code>{framework}</code> {html.escape(result['name'])}</summary>", ""]
            if result["message"].strip():
                block += ["```text", cut(result["message"], 4000), "```", ""]
            if result["stdout"].strip():
                block += ["```text", cut(result["stdout"], 6000), "```", ""]
            block.append("</details>")
            if result["message"].strip() or result["stdout"].strip():
                details.append("\n".join(block))

    for framework, result in failures[:MAX_ERRORS_AS_ANNOTATIONS]:
        text = cut(result["message"], MAX_ANNOTATION_TEXT)
        if result["stdout"].strip():
            text += "\n--- вывод теста ---\n" + cut(result["stdout"], MAX_ANNOTATION_TEXT)
        print(f"::error title={escape_property(framework + ' ' + result['name'])}::{escape_data(text)}")

    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as out:
            out.write("\n".join(summary) + "\n\n")
            if failures:
                out.write(f"**Упавших тестов: {len(failures)}.** Разверните блоки ниже.\n\n")
            if details:
                out.write("### Вывод тестов\n\n" + "\n\n".join(details) + "\n")

    print(f"[{title}] итого тестов во всех TRX: {grand_total}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
