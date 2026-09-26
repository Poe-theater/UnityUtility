
namespace UnityUtility.DataPersistence
{
    public interface IDataPersistence<TGameData>
    {
        void LoadData(TGameData data);

        void SaveData(ref TGameData data);
    }
}