namespace SimFS
{
    public interface IObjectPool<T>
    {
        T Get();
        void Return(T obj);
        int MaxCapacity { get; set; }
    }
}
