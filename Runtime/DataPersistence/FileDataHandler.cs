using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using System.Security.Cryptography;

namespace UnityUtility.DataPersistence
{
    public class FileDataHandler
    {
        private readonly string dataDirPath;
        private readonly string dataFileName;
        private readonly bool useEncryption;

        private const string SALT = "your-game-salt-change-this";
        private const int KEY_SIZE = 32;
        private const int HMAC_SIZE = 32;

        private const string TEMP_SUFFIX = ".tmp";
        private const string BACKUP_SUFFIX = ".bak";
        private const string CORRUPT_SUFFIX = ".corrupt";

        public FileDataHandler(string dataDirPath, string dataFileName, bool useEncryption)
        {
            this.dataDirPath = dataDirPath;
            this.dataFileName = dataFileName;
            this.useEncryption = useEncryption;
        }

        private string FullPath => Path.Combine(dataDirPath, dataFileName);
        private string TempPath => FullPath + TEMP_SUFFIX;
        private string BackupPath => FullPath + BACKUP_SUFFIX;

        #region Load

        /// <summary>
        /// Legge il save come JSON grezzo. La deserializzazione avviene a monte,
        /// dopo le eventuali migrazioni: qui il contenuto resta una stringa
        /// perché un formato vecchio può contenere campi che la classe dati
        /// corrente non ha più, e JsonUtility li scarterebbe in silenzio.
        /// </summary>
        public string LoadJson(out SaveReadStatus status)
        {
            string json = ReadFile(FullPath, out status);

            if (status == SaveReadStatus.Ok)
                return json;

            if (status == SaveReadStatus.NotFound)
            {
                // Il file principale non c'è ma un backup sì: probabile crash
                // durante la scrittura precedente.
                if (!File.Exists(BackupPath))
                    return null;
            }
            else
            {
                // File presente ma illeggibile: lo mettiamo da parte invece di
                // sovrascriverlo, così resta disponibile per un'analisi.
                QuarantineCorruptFile();
            }

            string backupJson = ReadFile(BackupPath, out SaveReadStatus backupStatus);

            if (backupStatus != SaveReadStatus.Ok)
                return null;

            Debug.LogWarning("[FileDataHandler] Save principale non valido: ripristinato dal backup.");

            status = SaveReadStatus.Ok;

            return backupJson;
        }

        /// <summary>
        /// Compatibilità con il vecchio utilizzo: legge e deserializza in un colpo.
        /// Non passa dalle migrazioni.
        /// </summary>
        public T Load<T>() where T : class
        {
            string json = LoadJson(out SaveReadStatus status);

            if (status != SaveReadStatus.Ok || string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonUtility.FromJson<T>(json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileDataHandler] JSON non deserializzabile: {ex}");
                return null;
            }
        }

        private string ReadFile(string path, out SaveReadStatus status)
        {
            if (!File.Exists(path))
            {
                status = SaveReadStatus.NotFound;
                return null;
            }

            try
            {
                byte[] fileBytes = File.ReadAllBytes(path);

                if (fileBytes.Length == 0)
                {
                    status = SaveReadStatus.Corrupted;
                    return null;
                }

                if (!useEncryption)
                {
                    status = SaveReadStatus.Ok;
                    return Encoding.UTF8.GetString(fileBytes);
                }

                if (fileBytes.Length < HMAC_SIZE)
                {
                    status = SaveReadStatus.Corrupted;
                    return null;
                }

                byte[] storedHmac = new byte[HMAC_SIZE];
                byte[] encryptedData = new byte[fileBytes.Length - HMAC_SIZE];

                Array.Copy(fileBytes, 0, storedHmac, 0, HMAC_SIZE);
                Array.Copy(fileBytes, HMAC_SIZE, encryptedData, 0, encryptedData.Length);

                byte[] key = GetKey();

                if (!storedHmac.SequenceEqual(ComputeHMAC(encryptedData, key)))
                {
                    Debug.LogError("[FileDataHandler] Save manomesso o corrotto (HMAC non valido).");
                    status = SaveReadStatus.Tampered;
                    return null;
                }

                status = SaveReadStatus.Ok;
                return Decrypt(encryptedData, key);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileDataHandler] Errore in lettura da {path}\n{ex}");
                status = SaveReadStatus.Corrupted;
                return null;
            }
        }

        private void QuarantineCorruptFile()
        {
            try
            {
                if (!File.Exists(FullPath))
                    return;

                string target = FullPath + CORRUPT_SUFFIX;

                if (File.Exists(target))
                    File.Delete(target);

                File.Move(FullPath, target);

                Debug.LogWarning($"[FileDataHandler] Save non valido spostato in {target}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileDataHandler] Impossibile mettere da parte il save corrotto: {ex}");
            }
        }

        #endregion

        #region Save

        /// <summary>
        /// Scrittura atomica: prima su file temporaneo, poi il vecchio save
        /// diventa backup e il temporaneo prende il suo posto. Se il processo
        /// viene ucciso a metà (su Android succede) resta sempre almeno un file
        /// integro fra principale e backup.
        /// </summary>
        public void SaveJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                Debug.LogError("[FileDataHandler] Tentativo di salvare un JSON vuoto: annullato.");
                return;
            }

            try
            {
                Directory.CreateDirectory(dataDirPath);

                byte[] fileBytes;

                if (useEncryption)
                {
                    byte[] key = GetKey();
                    byte[] encryptedData = Encrypt(json, key);
                    byte[] hmac = ComputeHMAC(encryptedData, key);

                    fileBytes = new byte[hmac.Length + encryptedData.Length];

                    Array.Copy(hmac, 0, fileBytes, 0, hmac.Length);
                    Array.Copy(encryptedData, 0, fileBytes, hmac.Length, encryptedData.Length);
                }
                else
                {
                    fileBytes = Encoding.UTF8.GetBytes(json);
                }

                File.WriteAllBytes(TempPath, fileBytes);

                if (File.Exists(FullPath))
                {
                    if (File.Exists(BackupPath))
                        File.Delete(BackupPath);

                    File.Move(FullPath, BackupPath);
                }

                File.Move(TempPath, FullPath);

#if UNITY_EDITOR
                Debug.Log($"[FileDataHandler] Salvato in: {FullPath}");
#endif
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileDataHandler] Errore in scrittura su {FullPath}\n{ex}");
            }
        }

        public void Save<T>(T data)
        {
            try
            {
                SaveJson(JsonUtility.ToJson(data, true));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileDataHandler] Serializzazione fallita: {ex}");
            }
        }

        #endregion

        #region Utility

        public bool SaveExists() => File.Exists(FullPath) || File.Exists(BackupPath);

        /// <summary>
        /// Cancella save, backup e temporanei. Da usare per un reset esplicito
        /// richiesto dall'utente, non come rimedio a un save incompatibile:
        /// per quello esistono le migrazioni.
        /// </summary>
        public void Delete()
        {
            TryDelete(FullPath);
            TryDelete(BackupPath);
            TryDelete(TempPath);
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileDataHandler] Impossibile cancellare {path}: {ex}");
            }
        }

        #endregion

        #region Crypto

        private byte[] GetKey()
        {
            string deviceId = SystemInfo.deviceUniqueIdentifier;

            using var derive = new Rfc2898DeriveBytes(
                deviceId,
                Encoding.UTF8.GetBytes(SALT),
                10000,
                HashAlgorithmName.SHA256
            );

            return derive.GetBytes(KEY_SIZE);
        }

        private byte[] Encrypt(string plainText, byte[] key)
        {
            using Aes aes = Aes.Create();
            aes.Key = key;
            aes.GenerateIV();

            using MemoryStream ms = new();

            ms.Write(aes.IV, 0, aes.IV.Length);

            using (CryptoStream cs = new(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            using (StreamWriter sw = new(cs))
            {
                sw.Write(plainText);
            }

            return ms.ToArray();
        }

        private string Decrypt(byte[] encryptedData, byte[] key)
        {
            using Aes aes = Aes.Create();
            aes.Key = key;

            byte[] iv = new byte[aes.BlockSize / 8];
            Array.Copy(encryptedData, iv, iv.Length);

            aes.IV = iv;

            using MemoryStream ms = new(encryptedData, iv.Length, encryptedData.Length - iv.Length);
            using CryptoStream cs = new(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using StreamReader sr = new(cs);

            return sr.ReadToEnd();
        }

        private byte[] ComputeHMAC(byte[] data, byte[] key)
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(data);
        }

        #endregion
    }
}