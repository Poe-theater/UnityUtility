using System;
using System.Linq;
using UnityEngine;
using System.Collections.Generic;

namespace UnityUtility.DataPersistence
{
    public class DataPersistenceManager<TGameData> where TGameData : class, new()
    {
        private FileDataHandler dataHandler;
        private List<IDataPersistence<TGameData>> dataPersistenceObjects;
        private readonly List<ISaveMigration> migrations = new();

        public Action OnLoad;
        public Action OnSave;
        public Action OnNewGame;

        /// Invocato dopo una migrazione riuscita: (versioneOrigine, versioneRaggiunta).
        public Action<int, int> OnMigrated;

        /// Invocato quando il save esisteva ma non è stato caricato.
        public Action<SaveReadStatus> OnLoadFailed;

        private bool isInitialized;

        public bool save = true;
        public TGameData gameData;
        public bool useEncryption = true;
        public string fileName = "save.dat";
        public bool autoUpdateDataPersistenceObjects = false;

        /// <summary>
        /// Versione del formato scritta nei nuovi salvataggi. Va incrementata
        /// a ogni cambiamento non retrocompatibile, registrando la migrazione
        /// corrispondente. Parte da 1 perché 0 identifica i save scritti prima
        /// dell'introduzione del versioning.
        /// </summary>
        public int currentSaveVersion = 1;

        public MigrationFailurePolicy migrationFailurePolicy = MigrationFailurePolicy.NewGame;
        public FutureSavePolicy futureSavePolicy = FutureSavePolicy.LoadWithoutSaving;

        /// <summary>
        /// True quando il file caricato proveniva da una versione più recente
        /// del gioco e la policy impone di non sovrascriverlo.
        /// </summary>
        public bool IsSaveLocked { get; private set; }

        /// Versione letta dall'ultimo file caricato (-1 se nessuna).
        public int LoadedSaveVersion { get; private set; } = -1;

        #region Init

        public void Init()
        {
            if (!save) return;

            dataHandler = new FileDataHandler(
                Application.persistentDataPath,
                fileName,
                useEncryption
            );

            dataPersistenceObjects = FindAllDataPersistenceObjects();
            isInitialized = true;
        }

        /// <summary>
        /// Applica il set di migrazioni del progetto: imposta la versione
        /// corrente e registra la catena. Va chiamato prima di StartGame().
        /// </summary>
        public void ApplyMigrationSet(SaveMigrationSet<TGameData> set)
        {
            if (set == null)
                return;

            set.Apply(this);
        }

        /// <summary>
        /// Le migrazioni vanno registrate prima di StartGame().
        /// </summary>
        public void RegisterMigration(ISaveMigration migration)
        {
            if (migration == null)
                return;

            if (migration.ToVersion <= migration.FromVersion)
            {
                Debug.LogError($"[DataPersistence] Migrazione non valida: {migration.FromVersion} -> {migration.ToVersion}.");
                return;
            }

            migrations.Add(migration);
        }

        public void RegisterMigration(int fromVersion, int toVersion, Func<string, string> migrate) =>
            RegisterMigration(new DelegateSaveMigration(fromVersion, toVersion, migrate));

        public void ClearMigrations() => migrations.Clear();

        #endregion

        #region Game flow

        public void StartGame()
        {
            if (!save || !isInitialized) return;

            LoadGame();
        }

        public void NewGame()
        {
            if (!save) return;

            gameData = new TGameData();

            StampVersion(gameData, currentSaveVersion);

            IsSaveLocked = false;
            LoadedSaveVersion = currentSaveVersion;

            OnNewGame?.Invoke();
        }

        public void LoadGame()
        {
            if (!save || !isInitialized) return;

            IsSaveLocked = false;
            LoadedSaveVersion = -1;

            string json = dataHandler.LoadJson(out SaveReadStatus status);

            if (status != SaveReadStatus.Ok || string.IsNullOrWhiteSpace(json))
            {
                if (status != SaveReadStatus.NotFound)
                {
                    Debug.LogWarning($"[DataPersistence] Save non caricabile ({status}): nuova partita.");
                    OnLoadFailed?.Invoke(status);
                }

                NewGame();
                DistributeLoadedData();
                return;
            }

            if (!PrepareJson(ref json))
            {
                NewGame();
                DistributeLoadedData();
                return;
            }

            TGameData loaded = Deserialize(json);

            if (loaded == null)
            {
                Debug.LogWarning("[DataPersistence] Deserializzazione fallita: nuova partita.");
                OnLoadFailed?.Invoke(SaveReadStatus.Corrupted);

                NewGame();
                DistributeLoadedData();
                return;
            }

            gameData = loaded;

            // Il file resta alla sua versione finché non viene riscritto: se il
            // save è bloccato (versione futura) non dobbiamo fingere che sia
            // stato convertito.
            if (!IsSaveLocked)
                StampVersion(gameData, currentSaveVersion);

            DistributeLoadedData();
        }

        /// <summary>
        /// Porta il JSON alla versione corrente. Restituisce false se il save
        /// va scartato del tutto.
        /// </summary>
        private bool PrepareJson(ref string json)
        {
            if (!SaveMigrator.TryReadVersion(json, out int version))
            {
                Debug.LogWarning("[DataPersistence] Versione del save illeggibile: nuova partita.");
                OnLoadFailed?.Invoke(SaveReadStatus.Corrupted);
                return false;
            }

            LoadedSaveVersion = version;

            if (version > currentSaveVersion)
                return HandleFutureSave(version);

            if (version == currentSaveVersion)
                return true;

            MigrationResult result = SaveMigrator.Migrate(
                json, version, currentSaveVersion, migrations,
                out string migrated, out int reached);

            switch (result)
            {
                case MigrationResult.NotNeeded:
                    return true;

                case MigrationResult.Migrated:
                    json = migrated;
                    OnMigrated?.Invoke(version, reached);
                    return true;

                default:
                    // Migrazione mancante o fallita: la catena può aver già
                    // fatto qualche passo, quindi ripartiamo dal JSON originale.
                    if (migrationFailurePolicy == MigrationFailurePolicy.LoadAsIs)
                    {
                        Debug.LogWarning($"[DataPersistence] Migrazione non riuscita dalla versione {version}: carico il save così com'è.");
                        return true;
                    }

                    Debug.LogWarning($"[DataPersistence] Migrazione non riuscita dalla versione {version}: nuova partita.");
                    return false;
            }
        }

        private bool HandleFutureSave(int version)
        {
            switch (futureSavePolicy)
            {
                case FutureSavePolicy.NewGame:
                    Debug.LogWarning($"[DataPersistence] Save della versione {version}, più recente di {currentSaveVersion}: scartato.");
                    return false;

                case FutureSavePolicy.Load:
                    Debug.LogWarning($"[DataPersistence] Save della versione {version}, più recente di {currentSaveVersion}: caricato, i campi sconosciuti andranno persi.");
                    return true;

                default:
                    Debug.LogWarning($"[DataPersistence] Save della versione {version}, più recente di {currentSaveVersion}: caricato in sola lettura.");
                    IsSaveLocked = true;
                    return true;
            }
        }

        private TGameData Deserialize(string json)
        {
            try
            {
                return JsonUtility.FromJson<TGameData>(json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DataPersistence] JSON non deserializzabile: {ex}");
                return null;
            }
        }

        private void DistributeLoadedData()
        {
            foreach (var obj in dataPersistenceObjects)
            {
                try
                {
                    obj.LoadData(gameData);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[DataPersistence] Errore nel caricamento di {obj}: {ex}");
                }
            }

            OnLoad?.Invoke();
        }

        public void SaveGame()
        {
            if (!save || !isInitialized) return;

            if (gameData == null)
            {
                Debug.LogWarning("[DataPersistence] SaveGame chiamato prima del caricamento: ignorato.");
                return;
            }

            if (IsSaveLocked)
            {
                // Sovrascrivere un save di una versione più recente ne
                // troncherebbe i campi sconosciuti.
                Debug.LogWarning("[DataPersistence] Salvataggio bloccato: il file appartiene a una versione più recente del gioco.");
                return;
            }

            if (autoUpdateDataPersistenceObjects)
                dataPersistenceObjects = FindAllDataPersistenceObjects();

            foreach (var obj in dataPersistenceObjects)
            {
                try
                {
                    obj.SaveData(ref gameData);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[DataPersistence] Errore nel salvataggio da {obj}: {ex}");
                }
            }

            StampVersion(gameData, currentSaveVersion);

            dataHandler.Save(gameData);

            OnSave?.Invoke();
        }

        #endregion

        #region Utility

        public bool HaveSaves() => save && dataHandler != null && dataHandler.SaveExists();

        /// <summary>
        /// Cancella il file. Reset esplicito richiesto dall'utente: per i save
        /// di formato vecchio si usano le migrazioni, non questo.
        /// </summary>
        public void DeleteSave()
        {
            if (!isInitialized) return;

            dataHandler.Delete();

            IsSaveLocked = false;
            LoadedSaveVersion = -1;
        }

        public void HandleApplicationPause(bool pauseStatus)
        {
            if (pauseStatus)
                SaveGame();
        }

        public void HandleApplicationQuit()
        {
            SaveGame();
        }

        public void UpdateReference()
        {
            dataPersistenceObjects = FindAllDataPersistenceObjects();
        }

        private static void StampVersion(TGameData data, int version)
        {
            if (data is IVersionedSaveData versioned)
                versioned.SaveVersion = version;
        }

        private List<IDataPersistence<TGameData>> FindAllDataPersistenceObjects()
        {
            var result = new List<IDataPersistence<TGameData>>();

            // Resources.FindObjectsOfTypeAll include per natura gli oggetti inattivi
            // e non è deprecata in nessuna versione: è l'unico modo per avere lo
            // stesso comportamento su Unity 6.3 e 6.6 senza API condizionali.
            foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (mb == null || mb is not IDataPersistence<TGameData> persistence)
                    continue;

                // In editor restituisce anche prefab e asset caricati: teniamo solo
                // ciò che appartiene davvero a una scena. Gli oggetti in
                // DontDestroyOnLoad hanno una scene valida, quindi passano.
                if (!mb.gameObject.scene.IsValid())
                    continue;

                if (mb.hideFlags != HideFlags.None)
                    continue;

                result.Add(persistence);
            }

            return result;
        }

        #endregion
    }
}