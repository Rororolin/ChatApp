// ============================================================
//  Shared/PasswordRules.cs — требования к паролю при регистрации
// ============================================================
using System.Linq;
using System.Text;

namespace ChatApp.Shared
{
    public static class PasswordRules
    {
        public const int RegistrationMinLength = 8;
        public const int MaxLength               = 128;

        /// <summary>Краткая подсказка для формы регистрации.</summary>
        public static string RequirementsSummaryRu { get; } =
            "Пароль при регистрации: не короче 8 символов; без русских букв; " +
            "латинские ЗАГЛАВНЫЕ и строчные буквы; хотя бы одна цифра; хотя бы один спецсимвол " +
            "(!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~ и аналогичные).";

        /// <summary>null если пароль подходит для регистрации.</summary>
        public static string? GetRegistrationError(string password)
        {
            if (string.IsNullOrEmpty(password))
                return "Введите пароль.";

            if (password.Length < RegistrationMinLength)
                return $"Пароль слишком короткий: минимум {RegistrationMinLength} символов.";

            if (password.Length > MaxLength)
                return $"Пароль слишком длинный: не больше {MaxLength} символов.";

            if (password.Any(c => c is >= '\u0400' and <= '\u04FF'))
                return "В пароле нельзя использовать русские буквы. Используйте латиницу и разрешённые символы.";

            bool upper = false, lower = false, digit = false, special = false;
            foreach (char c in password)
            {
                if (c is >= 'A' and <= 'Z') upper = true;
                else if (c is >= 'a' and <= 'z') lower = true;
                else if (c is >= '0' and <= '9') digit = true;
                else if (!char.IsWhiteSpace(c))
                    special = true;
            }

            var missing = new StringBuilder();
            if (!upper) missing.Append("заглавную латинскую букву (A–Z); ");
            if (!lower) missing.Append("строчную латинскую букву (a–z); ");
            if (!digit) missing.Append("цифру (0–9); ");
            if (!special) missing.Append("спецсимвол (например ! @ # $ % и т.п., не пробел); ");

            if (missing.Length > 0)
                return "Пароль не соответствует требованиям. Добавьте: " + missing.ToString().TrimEnd(' ', ';') + ".";

            return null;
        }
    }
}
