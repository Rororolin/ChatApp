// ============================================================
//  Shared/UsernameRules.cs — правила username (клиент + сервер)
// ============================================================

namespace ChatApp.Shared
{
    public static class UsernameRules
    {
        public const int MinLength = 2;
        public const int MaxLength = 20;

        public static string Normalize(string raw) => raw.Trim().ToLowerInvariant();

        /// <summary>Возвращает текст ошибки на русском или null, если формат допустим.</summary>
        public static string? GetValidationError(string normalized)
        {
            if (string.IsNullOrEmpty(normalized))
                return "Введите username.";

            if (normalized.Length < MinLength)
                return $"Username слишком короткий: нужно не меньше {MinLength} символов (латиница a–z, цифры, _).";

            if (normalized.Length > MaxLength)
                return $"Username слишком длинный: не больше {MaxLength} символов.";

            foreach (char c in normalized)
            {
                if (c is >= 'a' and <= 'z') continue;
                if (c is >= '0' and <= '9') continue;
                if (c == '_') continue;

                if (c is >= '\u0400' and <= '\u04FF')
                    return "В username нельзя использовать русские буквы. Допустимы только латиница (a–z), цифры 0–9 и символ «_».";

                return $"Недопустимый символ «{c}». Разрешены только латинские буквы a–z, цифры 0–9 и «_».";
            }

            return null;
        }

        public static bool IsValid(string normalized) => GetValidationError(normalized) == null;
    }
}
