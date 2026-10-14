// ============================================================
//  Shared/CryptoHelper.cs
//  Студент 3 — Модуль шифрования AES-128
// ============================================================
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ChatApp.Shared
{
    /// <summary>
    /// Симметричное шифрование AES-128 в режиме CBC.
    /// Ключ и IV — общий секрет (pre-shared key), известный всем
    /// участникам чата заранее (упрощённая модель для учебного проекта).
    ///
    /// Формат зашифрованного блока:
    ///   Base64( IV[16 байт] + CipherText )
    /// </summary>
    public static class CryptoHelper
    {
        // --- Общий секретный ключ 128 бит (16 байт) ---
        // В реальном приложении передаётся по защищённому каналу.
        private static readonly byte[] SharedKey = Encoding.UTF8.GetBytes("MySecretKey12345"); // ровно 16 символов

        /// <summary>
        /// Зашифровать строку.
        /// Возвращает Base64-строку: IV (16 б) + зашифрованный текст.
        /// </summary>
        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return plainText;

            using var aes = Aes.Create();
            aes.KeySize  = 128;
            aes.Key      = SharedKey;
            aes.Mode     = CipherMode.CBC;
            aes.Padding  = PaddingMode.PKCS7;
            aes.GenerateIV();                       // случайный IV для каждого сообщения

            using var encryptor = aes.CreateEncryptor();
            using var ms        = new MemoryStream();

            // Записываем IV в начало потока
            ms.Write(aes.IV, 0, aes.IV.Length);

            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            using (var sw = new StreamWriter(cs, Encoding.UTF8))
            {
                sw.Write(plainText);
            }

            return Convert.ToBase64String(ms.ToArray());
        }

        /// <summary>
        /// Расшифровать Base64-строку, полученную от Encrypt().
        /// </summary>
        public static string Decrypt(string cipherBase64)
        {
            if (string.IsNullOrEmpty(cipherBase64))
                return cipherBase64;

            byte[] fullData = Convert.FromBase64String(cipherBase64);

            using var aes = Aes.Create();
            aes.KeySize  = 128;
            aes.Key      = SharedKey;
            aes.Mode     = CipherMode.CBC;
            aes.Padding  = PaddingMode.PKCS7;

            // Первые 16 байт — IV
            byte[] iv         = new byte[16];
            byte[] cipherText = new byte[fullData.Length - 16];
            Array.Copy(fullData, 0,  iv,         0, 16);
            Array.Copy(fullData, 16, cipherText,  0, cipherText.Length);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            using var ms        = new MemoryStream(cipherText);
            using var cs        = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var sr        = new StreamReader(cs, Encoding.UTF8);

            return sr.ReadToEnd();
        }
    }
}
