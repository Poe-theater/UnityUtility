using System;
using UnityEngine;
using System.Collections.Generic;

namespace UnityUtility.DataPersistence
{
    /// <summary>
    /// Implementata dalla classe dati di gioco per esporre la versione del
    /// formato. Il controllo è a runtime e non un vincolo generico: i progetti
    /// che non usano il versioning continuano a funzionare senza modifiche.
    /// </summary>
    public interface IVersionedSaveData
    {
        int SaveVersion { get; set; }
    }

    /// <summary>
    /// Un passo della catena di migrazioni. Lavora sul JSON grezzo e non
    /// sull'oggetto deserializzato: JsonUtility scarta i campi che non esistono
    /// più nella classe corrente, quindi qualunque rinomina o rimozione sarebbe
    /// già persa prima di arrivare qui.
    /// </summary>
    public interface ISaveMigration
    {
        int FromVersion { get; }
        int ToVersion { get; }

        /// <summary>
        /// Riceve il JSON nel formato FromVersion e lo restituisce nel formato
        /// ToVersion. Può lanciare: il chiamante gestisce l'errore secondo la
        /// policy configurata.
        /// </summary>
        string Migrate(string json);
    }

    /// <summary>
    /// Migrazione definita al volo da una lambda, per i casi banali.
    /// </summary>
    public class DelegateSaveMigration : ISaveMigration
    {
        private readonly Func<string, string> migrate;

        public int FromVersion { get; }
        public int ToVersion { get; }

        public DelegateSaveMigration(int fromVersion, int toVersion, Func<string, string> migrate)
        {
            FromVersion = fromVersion;
            ToVersion = toVersion;
            this.migrate = migrate ?? throw new ArgumentNullException(nameof(migrate));
        }

        public string Migrate(string json) => migrate(json);
    }

    /// <summary>
    /// Migrazione fra due classi tipizzate, tipicamente due DTO congelati che
    /// descrivono il formato di due versioni consecutive.
    ///
    /// È il modo corretto di scrivere una migrazione destinata a durare: la
    /// classe dati viva continua a cambiare nel tempo, mentre un DTO congelato
    /// descrive per sempre com'era il formato in quel momento. Una migrazione
    /// che legge la classe viva invece smette di essere corretta appena la
    /// classe cambia.
    /// </summary>
    public abstract class TypedSaveMigration<TFrom, TTo> : ISaveMigration
        where TFrom : class, new()
        where TTo : class
    {
        public int FromVersion { get; }
        public int ToVersion { get; }

        protected TypedSaveMigration(int fromVersion, int toVersion)
        {
            FromVersion = fromVersion;
            ToVersion = toVersion;
        }

        /// <summary>
        /// Costruisce i dati nel formato nuovo a partire da quelli vecchi.
        /// Quello che non viene copiato è, per definizione, quello che si
        /// decide di non conservare.
        /// </summary>
        protected abstract TTo Convert(TFrom old);

        public string Migrate(string json)
        {
            TFrom old = JsonUtility.FromJson<TFrom>(json) ?? new TFrom();

            TTo result = Convert(old);

            if (result == null)
                throw new InvalidOperationException(
                    $"La migrazione {FromVersion} -> {ToVersion} ha restituito null.");

            if (result is IVersionedSaveData versioned)
                versioned.SaveVersion = ToVersion;

            return JsonUtility.ToJson(result, true);
        }
    }

    /// <summary>
    /// Raggruppa le migrazioni di un progetto e la versione corrente del suo
    /// formato. Ogni gioco ne implementa una e la passa al manager: è l'unico
    /// punto di contatto fra la libreria e il formato specifico.
    /// </summary>
    public abstract class SaveMigrationSet<TGameData> where TGameData : class, new()
    {
        /// Versione scritta dai salvataggi della build corrente.
        public abstract int CurrentVersion { get; }

        /// Registra qui tutte le migrazioni, in ordine crescente.
        protected abstract void Register(DataPersistenceManager<TGameData> manager);

        public void Apply(DataPersistenceManager<TGameData> manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));

            manager.currentSaveVersion = CurrentVersion;

            Register(manager);
        }
    }

    public enum SaveReadStatus
    {
        Ok,
        NotFound,
        Corrupted,
        Tampered
    }

    public enum MigrationResult
    {
        NotNeeded,
        Migrated,
        NoPathAvailable,
        Failed
    }

    /// <summary>
    /// Cosa fare quando il save non può essere portato alla versione corrente.
    /// </summary>
    public enum MigrationFailurePolicy
    {
        /// Parte una partita nuova. Sicuro, ma perde anche i dati durevoli.
        NewGame,

        /// Deserializza comunque il JSON vecchio: i campi ancora presenti nella
        /// classe corrente sopravvivono, gli altri restano al valore di default.
        /// Adatto ai cambiamenti puramente additivi.
        LoadAsIs
    }

    /// <summary>
    /// Cosa fare con un save scritto da una versione più recente del gioco.
    /// Succede con i rollout graduali degli store, quando un utente torna
    /// indietro di versione.
    /// </summary>
    public enum FutureSavePolicy
    {
        /// Carica quello che riesce a leggere ma blocca i salvataggi: evita di
        /// sovrascrivere il file troncando i campi che questa versione non conosce.
        LoadWithoutSaving,

        /// Carica e continua normalmente. I campi sconosciuti verranno persi
        /// al primo salvataggio.
        Load,

        /// Ignora il file e parte da zero. Sconsigliato: è distruttivo.
        NewGame
    }

    /// <summary>
    /// Esegue la catena di migrazioni da una versione all'altra.
    /// </summary>
    public static class SaveMigrator
    {
        private const int MAX_STEPS = 64;

        [Serializable]
        private class VersionProbe
        {
            public int saveVersion;
        }

        /// <summary>
        /// Legge la versione dal JSON senza deserializzare l'intero oggetto.
        /// Restituisce false se il JSON non è leggibile. Un save scritto prima
        /// dell'introduzione del versioning non ha il campo e vale 0.
        /// </summary>
        public static bool TryReadVersion(string json, out int version)
        {
            version = -1;

            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                VersionProbe probe = JsonUtility.FromJson<VersionProbe>(json);

                if (probe == null)
                    return false;

                version = probe.saveVersion;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SaveMigrator] Impossibile leggere la versione del save: {ex.Message}");
                return false;
            }
        }

        public static MigrationResult Migrate(
            string json,
            int fromVersion,
            int targetVersion,
            IReadOnlyList<ISaveMigration> migrations,
            out string migratedJson,
            out int reachedVersion)
        {
            migratedJson = json;
            reachedVersion = fromVersion;

            if (fromVersion >= targetVersion)
                return MigrationResult.NotNeeded;

            if (migrations == null || migrations.Count == 0)
                return MigrationResult.NoPathAvailable;

            int steps = 0;

            while (reachedVersion < targetVersion)
            {
                if (++steps > MAX_STEPS)
                {
                    Debug.LogError("[SaveMigrator] Troppi passi di migrazione: catena probabilmente ciclica.");
                    return MigrationResult.Failed;
                }

                ISaveMigration step = FindStep(migrations, reachedVersion);

                if (step == null)
                {
                    Debug.LogWarning($"[SaveMigrator] Nessuna migrazione disponibile dalla versione {reachedVersion}.");
                    return MigrationResult.NoPathAvailable;
                }

                try
                {
                    migratedJson = step.Migrate(migratedJson);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SaveMigrator] Migrazione {step.FromVersion} -> {step.ToVersion} fallita: {ex}");
                    return MigrationResult.Failed;
                }

                if (string.IsNullOrWhiteSpace(migratedJson))
                {
                    Debug.LogError($"[SaveMigrator] La migrazione {step.FromVersion} -> {step.ToVersion} ha prodotto un JSON vuoto.");
                    return MigrationResult.Failed;
                }

                Debug.Log($"[SaveMigrator] Save migrato {step.FromVersion} -> {step.ToVersion}.");

                reachedVersion = step.ToVersion;
            }

            return MigrationResult.Migrated;
        }

        private static ISaveMigration FindStep(IReadOnlyList<ISaveMigration> migrations, int fromVersion)
        {
            ISaveMigration best = null;

            foreach (var m in migrations)
            {
                if (m == null || m.FromVersion != fromVersion)
                    continue;

                // ToVersion deve avanzare, altrimenti la catena non termina.
                if (m.ToVersion <= m.FromVersion)
                {
                    Debug.LogError($"[SaveMigrator] Migrazione non valida: {m.FromVersion} -> {m.ToVersion}.");
                    continue;
                }

                // A parità di partenza vince il salto più lungo: permette di
                // registrare scorciatoie senza rimuovere i passi intermedi.
                if (best == null || m.ToVersion > best.ToVersion)
                    best = m;
            }

            return best;
        }
    }
}