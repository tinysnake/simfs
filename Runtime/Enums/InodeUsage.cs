namespace SimFS
{
    public enum InodeUsage : byte
    {
        Unused,
        NormalFile,
        TinyFile,
        Directory,
    }
}
