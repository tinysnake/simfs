namespace SimFS
{
    public static class SimLog
    {
        public static void Info(string str)
        {
#if UNITY_2017_1_OR_NEWER
            UnityEngine.Debug.Log(str);
#else
            System.Console.WriteLine(str);
#endif
        }

        public static void Info(object obj)
        {
#if UNITY_2017_1_OR_NEWER
            UnityEngine.Debug.Log(obj);
#else
            System.Console.WriteLine(obj);
#endif
        }
    }
}
