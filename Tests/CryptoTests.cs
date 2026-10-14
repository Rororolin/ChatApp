// ============================================================
//  Tests/CryptoTests.cs
//  Студент 3 — Тестирование шифрования AES-128
// ============================================================
//
//  Запуск без фреймворка — обычная консольная программа.
//  Добавьте в проект Tests.csproj и запустите dotnet run.
//
using System;
using ChatApp.Shared;

namespace ChatApp.Tests
{
    class CryptoTests
    {
        static int _pass = 0, _fail = 0;

        static void Main()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("╔══════════════════════════════════════╗");
            Console.WriteLine("║   ChatApp — Тесты шифрования AES-128 ║");
            Console.WriteLine("╚══════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();

            // ---- Тест 1: Базовое шифрование/расшифровка ----
            Test("Базовое шифрование", () =>
            {
                string plain  = "Привет, мир!";
                string cipher = CryptoHelper.Encrypt(plain);
                string result = CryptoHelper.Decrypt(cipher);
                Assert(result == plain, $"'{result}' != '{plain}'");
            });

            // ---- Тест 2: Разные IV при одинаковом тексте ----
            Test("Разные IV (уникальность шифра)", () =>
            {
                string plain   = "test message";
                string cipher1 = CryptoHelper.Encrypt(plain);
                string cipher2 = CryptoHelper.Encrypt(plain);
                Assert(cipher1 != cipher2, "Одинаковые шифрограммы — уязвимость!");
            });

            // ---- Тест 3: Пустая строка ----
            Test("Пустая строка", () =>
            {
                string result = CryptoHelper.Encrypt("");
                Assert(result == "", "Пустая строка должна вернуть пустую");
            });

            // ---- Тест 4: Длинное сообщение ----
            Test("Длинное сообщение (500 символов)", () =>
            {
                string plain  = new string('А', 500);
                string cipher = CryptoHelper.Encrypt(plain);
                string result = CryptoHelper.Decrypt(cipher);
                Assert(result == plain, "Несовпадение длинного текста");
            });

            // ---- Тест 5: Спецсимволы и JSON ----
            Test("JSON и спецсимволы", () =>
            {
                string plain  = "{\"user\":\"Alice\",\"text\":\"Hello\\nWorld!\"}";
                string cipher = CryptoHelper.Encrypt(plain);
                string result = CryptoHelper.Decrypt(cipher);
                Assert(result == plain, $"Ошибка JSON: '{result}'");
            });

            // ---- Тест 6: Сериализация ChatPacket ----
            Test("Сериализация ChatPacket (JSON round-trip)", () =>
            {
                var pkt = new ChatPacket
                {
                    Sender  = "Bob",
                    Message = CryptoHelper.Encrypt("Секретное сообщение"),
                    Type    = "msg"
                };
                string json   = pkt.ToJson();
                var    pkt2   = ChatPacket.FromJson(json);
                Assert(pkt2 != null, "FromJson вернул null");
                string plain  = CryptoHelper.Decrypt(pkt2!.Message);
                Assert(plain == "Секретное сообщение", $"Ошибка: '{plain}'");
            });

            // ---- Тест 7: Неверный шифртекст ----
            Test("Неверный шифртекст — ожидаем исключение", () =>
            {
                bool threw = false;
                try { CryptoHelper.Decrypt("не_base64_данные!!!"); }
                catch { threw = true; }
                Assert(threw, "Должно было броситься исключение");
            });

            // ---- Итог ----
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"Результат: {_pass} пройдено, {_fail} провалено");
            Console.ResetColor();
            if (_fail == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✓ Все тесты пройдены!");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("✗ Есть ошибки!");
            }
            Console.ResetColor();
        }

        static void Test(string name, Action body)
        {
            Console.Write($"  [{name}] ... ");
            try
            {
                body();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS");
                Console.ResetColor();
                _pass++;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL: {ex.Message}");
                Console.ResetColor();
                _fail++;
            }
        }

        static void Assert(bool cond, string msg)
        {
            if (!cond) throw new Exception(msg);
        }
    }
}
