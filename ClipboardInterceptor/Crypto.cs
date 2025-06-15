using System.Security.Cryptography;
using System.Text;
using System.Drawing.Imaging;

namespace ClipboardInterceptor
{
    static class Crypto
    {
        // Nama file untuk menyimpan kunci dan PIN (terenkripsi oleh DPAPI)
        private const string KeyFile = "key.dat";
        private const string PinFile = "pin.dat";
        private static readonly byte[] masterKey;
        private static readonly object keyRotationLock = new object();
        private static DateTime lastKeyRotation;
        private const int KEY_ROTATION_DAYS = 30;
        private static System.Threading.Timer keyRotationTimer;

        // Authentication failures tracking (untuk anti-brute force)
        private static int failedAttempts = 0;
        private static DateTime lastFailedAttempt = DateTime.MinValue;
        private const int MAX_FAILED_ATTEMPTS = 5;
        private const int LOCKOUT_MINUTES = 15;

        static Crypto()
        {
            // === Inisialisasi key (di-protect dengan DPAPI CurrentUser) ===
            if (File.Exists(KeyFile))
            {
                var protectedKey = File.ReadAllBytes(KeyFile);
                masterKey = ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);

                // Check key age
                if (File.GetLastWriteTime(KeyFile).AddDays(KEY_ROTATION_DAYS) < DateTime.Now)
                {
                    RotateKey();
                }
            }
            else
            {
                // generate 128-bit key
                masterKey = new byte[16];
                RandomNumberGenerator.Fill(masterKey);
                SaveMasterKey();
            }

            // Setup key rotation timer
            keyRotationTimer = new System.Threading.Timer(_ => RotateKey(), null,
                TimeSpan.FromDays(1), TimeSpan.FromDays(1));

            lastKeyRotation = File.GetLastWriteTime(KeyFile);
        }

        private static void SaveMasterKey()
        {
            var protectedKey = ProtectedData.Protect(masterKey, null, DataProtectionScope.CurrentUser);

            // Secure file write with backup
            string tempFile = KeyFile + ".tmp";
            File.WriteAllBytes(tempFile, protectedKey);

            if (File.Exists(KeyFile))
                File.Replace(tempFile, KeyFile, KeyFile + ".bak");
            else
                File.Move(tempFile, KeyFile);
        }

        private static void RotateKey()
        {
            lock (keyRotationLock)
            {
                // Only rotate once per rotation period
                if (DateTime.Now - lastKeyRotation < TimeSpan.FromDays(KEY_ROTATION_DAYS))
                    return;

                try
                {
                    // Create new key but derive it partially from old one for continuity
                    byte[] newKey = new byte[16];
                    RandomNumberGenerator.Fill(newKey);

                    // Mix in old key for better continuity
                    for (int i = 0; i < 8; i++)
                    {
                        newKey[i] ^= masterKey[i + 8];
                    }

                    // Save to file
                    var protectedKey = ProtectedData.Protect(newKey, null, DataProtectionScope.CurrentUser);
                    File.WriteAllBytes(KeyFile, protectedKey);

                    // Update in memory
                    Array.Copy(newKey, masterKey, 16);
                    lastKeyRotation = DateTime.Now;
                }
                catch (Exception ex)
                {
                    // Log error
                    Console.WriteLine($"Key rotation failed: {ex.Message}");
                }
            }
        }

        // Check if PIN is set
        public static bool IsPinSet()
        {
            return File.Exists(PinFile);
        }

        // Set PIN
        public static void SetPIN(string pin)
        {
            if (string.IsNullOrEmpty(pin) || pin.Length != 4)
                throw new ArgumentException("PIN must be exactly 4 digits");

            byte[] pinBytes = Encoding.UTF8.GetBytes(pin);
            byte[] protectedPin = ProtectedData.Protect(pinBytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(PinFile, protectedPin);

            // Reset failed attempts
            failedAttempts = 0;
        }

        // Verify PIN with anti-brute force protection
        public static bool VerifyPIN(string pin)
        {
            if (!File.Exists(PinFile))
                return false;

            // Check if locked out
            if (failedAttempts >= MAX_FAILED_ATTEMPTS)
            {
                TimeSpan elapsed = DateTime.Now - lastFailedAttempt;
                if (elapsed.TotalMinutes < LOCKOUT_MINUTES)
                {
                    // Still locked out
                    return false;
                }
                else
                {
                    // Lockout period over
                    failedAttempts = 0;
                }
            }

            try
            {
                byte[] protectedPin = File.ReadAllBytes(PinFile);
                byte[] storedPin = ProtectedData.Unprotect(protectedPin, null, DataProtectionScope.CurrentUser);
                string storedPinString = Encoding.UTF8.GetString(storedPin);

                bool isValid = pin == storedPinString;

                if (isValid)
                {
                    // Reset failed attempts on success
                    failedAttempts = 0;
                    return true;
                }
                else
                {
                    // Track failed attempt
                    failedAttempts++;
                    lastFailedAttempt = DateTime.Now;
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        // Get lockout status
        public static (bool isLocked, int remainingMinutes) GetLockoutStatus()
        {
            if (failedAttempts >= MAX_FAILED_ATTEMPTS)
            {
                TimeSpan elapsed = DateTime.Now - lastFailedAttempt;
                if (elapsed.TotalMinutes < LOCKOUT_MINUTES)
                {
                    int remainingMinutes = LOCKOUT_MINUTES - (int)elapsed.TotalMinutes;
                    return (true, remainingMinutes);
                }
            }

            return (false, 0);
        }

        // Format hasil enkripsi: [nonce (12)] [tag (16)] [ciphertext]
        public static byte[] Encrypt(string plaintext, string associatedData = null)
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
            byte[] nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
            RandomNumberGenerator.Fill(nonce);

            byte[] cipherBytes = new byte[plainBytes.Length];
            byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize];   // 16 bytes

            // Tambahkan associated data jika tersedia
            byte[] associatedBytes = associatedData != null ?
                Encoding.UTF8.GetBytes(associatedData) : null;

            using var aes = new AesGcm(masterKey);
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag, associatedBytes);

            // Securely clean up plaintext from memory
            Array.Clear(plainBytes, 0, plainBytes.Length);

            return nonce
                .Concat(tag)
                .Concat(cipherBytes)
                .ToArray();
        }

        public static string Decrypt(byte[] data, string associatedData = null)
        {
            int nonceLen = AesGcm.NonceByteSizes.MaxSize;
            int tagLen = AesGcm.TagByteSizes.MaxSize;

            byte[] nonce = data.Take(nonceLen).ToArray();
            byte[] tag = data.Skip(nonceLen).Take(tagLen).ToArray();
            byte[] cipher = data.Skip(nonceLen + tagLen).ToArray();

            // Tambahkan associated data jika tersedia
            byte[] associatedBytes = associatedData != null ?
                Encoding.UTF8.GetBytes(associatedData) : null;

            byte[] plainBytes = new byte[cipher.Length];
            using var aes = new AesGcm(masterKey);
            aes.Decrypt(nonce, cipher, tag, plainBytes, associatedBytes);

            string result = Encoding.UTF8.GetString(plainBytes);

            // Securely clean up plain bytes from memory
            Array.Clear(plainBytes, 0, plainBytes.Length);

            return result;
        }

        // Encrypt image - returns base64 encoded encrypted data
        public static string EncryptImage(Image image, string associatedData = null)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                image.Save(ms, ImageFormat.Png);
                byte[] imageBytes = ms.ToArray();

                byte[] nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
                RandomNumberGenerator.Fill(nonce);

                byte[] cipherBytes = new byte[imageBytes.Length];
                byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize];

                byte[] associatedBytes = associatedData != null ?
                    Encoding.UTF8.GetBytes(associatedData) : null;

                using (var aes = new AesGcm(masterKey))
                {
                    aes.Encrypt(nonce, imageBytes, cipherBytes, tag, associatedBytes);
                }

                // Clean up original data
                Array.Clear(imageBytes, 0, imageBytes.Length);

                byte[] encryptedFull = nonce.Concat(tag).Concat(cipherBytes).ToArray();
                return Convert.ToBase64String(encryptedFull);
            }
        }

        // Decrypt image from base64 encoded encrypted data
        public static Image DecryptImage(string encryptedBase64, string associatedData = null)
        {
            byte[] encryptedData = Convert.FromBase64String(encryptedBase64);

            int nonceLen = AesGcm.NonceByteSizes.MaxSize;
            int tagLen = AesGcm.TagByteSizes.MaxSize;

            byte[] nonce = encryptedData.Take(nonceLen).ToArray();
            byte[] tag = encryptedData.Skip(nonceLen).Take(tagLen).ToArray();
            byte[] cipher = encryptedData.Skip(nonceLen + tagLen).ToArray();

            byte[] associatedBytes = associatedData != null ?
                Encoding.UTF8.GetBytes(associatedData) : null;

            byte[] decryptedBytes = new byte[cipher.Length];

            using (var aes = new AesGcm(masterKey))
            {
                aes.Decrypt(nonce, cipher, tag, decryptedBytes, associatedBytes);
            }

            using (MemoryStream ms = new MemoryStream(decryptedBytes))
            {
                Image result = Image.FromStream(ms);

                // Clean up decrypted bytes
                Array.Clear(decryptedBytes, 0, decryptedBytes.Length);

                return result;
            }
        }

        // Encrypt file paths
        public static string EncryptFilePaths(string[] filePaths, string associatedData = null)
        {
            string combinedPaths = string.Join("|", filePaths);
            byte[] encryptedData = Encrypt(combinedPaths, associatedData);
            return Convert.ToBase64String(encryptedData);
        }

        // Decrypt file paths
        public static string[] DecryptFilePaths(string encryptedBase64, string associatedData = null)
        {
            byte[] encryptedData = Convert.FromBase64String(encryptedBase64);
            string decryptedPaths = Decrypt(encryptedData, associatedData);
            return decryptedPaths.Split('|');
        }

        // Create short encrypted preview for text
        public static string CreateEncryptedPreview(string text, int maxLength = 30)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            string preview = text.Length <= maxLength ?
                text : text.Substring(0, maxLength) + "...";

            // Simple obfuscation (not true encryption but helps hide plaintext)
            StringBuilder obfuscated = new StringBuilder();

            for (int i = 0; i < preview.Length; i++)
            {
                char c = preview[i];

                if (i % 3 == 0 && char.IsLetterOrDigit(c))
                    obfuscated.Append('•');
                else if (i % 5 == 0)
                    obfuscated.Append('*');
                else
                    obfuscated.Append(c);
            }

            return obfuscated.ToString();
        }
    }
}